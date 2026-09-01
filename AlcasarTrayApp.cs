using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace AlcasarTray
{
    public class AlcasarTrayApp : ApplicationContext
    {
        private readonly NotifyIcon notifyIcon;
        private readonly HttpClientHandler handler;
        private readonly HttpClient client;
        private System.Windows.Forms.Timer timer;
        private readonly string configPath;
        private AlcasarConfig config = new AlcasarConfig();
        private Form hiddenForm;
        private bool? lastConnected;
        private bool manuallyDisconnected;

        public AlcasarTrayApp()
        {
            // Créer une fenêtre invisible pour maintenir l'app active
            hiddenForm = new Form
            {
                ShowInTaskbar = false,
                WindowState = FormWindowState.Minimized,
                FormBorderStyle = FormBorderStyle.None,
                Size = new Size(0, 0)
            };
            hiddenForm.Show();
            hiddenForm.Hide();

            // Configuration
            configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AlcasarTray",
                "config.json"
            );

            LoadConfig();

            // HttpClient avec gestion des cookies
            handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) => true // Pour développement
            };

            client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            // NotifyIcon (icône systray)
            notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Error,
                Visible = true,
                Text = "Alcasar Tray - Déconnecté"
            };

            // Menu contextuel
            var menu = new ContextMenuStrip();
            menu.Items.Add("État", null, (_, _) => ShowStatus());
            menu.Items.Add("Historique des connexions", null, async (_, _) => await ShowConnectionHistoryAsync());
            menu.Items.Add("-");
            menu.Items.Add("Reconnecter", null, async (_, _) => await KeepAliveAsync(promptIfMissing: true));
            menu.Items.Add("Déconnecter", null, async (_, _) => await LogoffAsync());
            menu.Items.Add("Configurer", null, (_, _) => ShowConfigDialog());
            menu.Items.Add("Changer d'utilisateur", null, async (_, _) => await ChangeUserAsync());
            menu.Items.Add("-");
            menu.Items.Add("Quitter", null, (_, _) => Application.Exit());

            notifyIcon.ContextMenuStrip = menu;
            notifyIcon.DoubleClick += async (_, _) => await KeepAliveAsync(promptIfMissing: true);

            // Timer de vérification
            timer = new System.Windows.Forms.Timer();
            timer.Interval = config.CheckIntervalSeconds * 1000;
            timer.Tick += async (_, _) => await KeepAliveAsync();
            timer.Start();

            // Vérification initiale : demande les identifiants si absents (premier lancement)
            _ = KeepAliveAsync(promptIfMissing: true);
        }

        private async Task ChangeUserAsync()
        {
            config.Username = "";
            config.Password = "";
            SaveConfig();
            await KeepAliveAsync(promptIfMissing: true);
        }

        // Le logoff Alcasar/chilli se fait sur un port dédié (3991 par convention), pas via intercept.php.
        private async Task LogoffAsync()
        {
            try
            {
                var host = new Uri(config.PortalUrl).Host;
                var logoffUrl = new UriBuilder("https", host, 3991, "logoff").Uri;
                await client.GetAsync(logoffUrl);
                manuallyDisconnected = true;
                SetStatus("Déconnecté", connected: false);
            }
            catch (Exception ex)
            {
                SetStatus($"Erreur déconnexion: {ex.Message}");
            }
        }

        private void LoadConfig()
        {
            var directory = Path.GetDirectoryName(configPath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                config = JsonSerializer.Deserialize<AlcasarConfig>(json) ?? new AlcasarConfig();
                config.Password = UnprotectPassword(config.EncryptedPassword);
            }
            else
            {
                config = new AlcasarConfig();
                SaveConfig();
            }
        }

        private void SaveConfig()
        {
            var directory = Path.GetDirectoryName(configPath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            config.EncryptedPassword = ProtectPassword(config.Password);
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        }

        // DPAPI : chiffre le mot de passe pour l'utilisateur Windows courant, sans clé à gérer.
        // Illisible pour un autre utilisateur ou sur une autre machine (par design).
        private static string ProtectPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return "";
            }
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        private static string UnprotectPassword(string encryptedPassword)
        {
            if (string.IsNullOrEmpty(encryptedPassword))
            {
                return "";
            }
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(encryptedPassword), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (CryptographicException)
            {
                return "";
            }
        }

        public async Task KeepAliveAsync(bool promptIfMissing = false)
        {
            // Un déclencheur interactif (double-clic, "Reconnecter") vaut consentement explicite
            // à se reconnecter ; sinon le timer de fond respecte la déconnexion manuelle.
            if (promptIfMissing)
            {
                manuallyDisconnected = false;
            }
            else if (manuallyDisconnected)
            {
                return;
            }

            if (string.IsNullOrEmpty(config.PortalUrl))
            {
                SetStatus("Configuration manquante");
                return;
            }

            if (promptIfMissing && (string.IsNullOrEmpty(config.Username) || string.IsNullOrEmpty(config.Password)))
            {
                ShowConfigDialog();
            }

            if (string.IsNullOrEmpty(config.Username) || string.IsNullOrEmpty(config.Password))
            {
                SetStatus("Authentification requise");
                return;
            }

            try
            {
                SetStatus("Vérification...", connected: null);

                // La page racine (index.php) n'est qu'un statut : le vrai point d'entrée,
                // qui redirige vers le formulaire ou la confirmation "déjà connecté", est intercept.php.
                var interceptUrl = new Uri(new Uri(config.PortalUrl), "intercept.php");
                var response = await client.GetAsync(interceptUrl);
                var html = await response.Content.ReadAsStringAsync();

                if (IsLoginPage(html))
                {
                    await LoginAsync(interceptUrl, html, config.Username, config.Password);
                }
                else
                {
                    SetStatus("Connecté ✓", connected: true);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Erreur: {ex.Message}");
            }
        }

        // Le formulaire de login d'intercept.php (Alcasar) contient toujours ce champ.
        private bool IsLoginPage(string html)
        {
            return html.Contains("name=\"username\"", StringComparison.Ordinal);
        }

        // intercept.php calcule lui-même le hash CHAP côté serveur : on lui repasse
        // simplement les champs cachés qu'il a fournis avec username/password en clair.
        private static string? ExtractHiddenField(string html, string fieldName)
        {
            var match = Regex.Match(html, $"name=\"{fieldName}\"\\s+value=\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : null;
        }

        private async Task LoginAsync(Uri interceptUrl, string loginPageHtml, string username, string password)
        {
            try
            {
                var formData = new Dictionary<string, string>
                {
                    ["username"] = username,
                    ["password"] = password,
                    ["button"] = "Authentication"
                };

                foreach (var field in new[] { "challenge", "userurl" })
                {
                    var value = ExtractHiddenField(loginPageHtml, field);
                    if (value != null)
                    {
                        formData[field] = value;
                    }
                }

                var content = new FormUrlEncodedContent(formData);
                var response = await client.PostAsync(interceptUrl, content);
                var resultHtml = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode && !IsLoginPage(resultHtml))
                {
                    SetStatus("Connecté ✓", connected: true);
                }
                else
                {
                    SetStatus("Échec authentification");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Erreur login: {ex.Message}");
            }
        }

        // connected: null pendant une vérification en cours, pour ne pas faire clignoter
        // l'icône avant qu'un état définitif (connecté ou non) soit confirmé.
        private void SetStatus(string status, bool? connected = false)
        {
            notifyIcon.Text = $"Alcasar Tray - {status}";
            if (connected.HasValue && connected != lastConnected)
            {
                notifyIcon.Icon = connected.Value ? SystemIcons.Information : SystemIcons.Error;
                lastConnected = connected;
            }
        }

        private void ShowStatus()
        {
            MessageBox.Show(
                $"URL: {config.PortalUrl}\n" +
                $"Intervalle: {config.CheckIntervalSeconds}s\n" +
                $"État: {notifyIcon.Text}",
                "État Alcasar Tray"
            );
        }

        // L'historique est affiché par index.php (page racine), sous forme de <li>"date (durée)"</li>.
        private async Task ShowConnectionHistoryAsync()
        {
            try
            {
                var response = await client.GetAsync(config.PortalUrl);
                var html = await response.Content.ReadAsStringAsync();
                var entries = Regex.Matches(html, "<li[^>]*>([^<]+)</li>")
                    .Select(m => m.Groups[1].Value.Trim())
                    .ToList();

                var grid = new DataGridView
                {
                    Dock = DockStyle.Fill,
                    ReadOnly = true,
                    AllowUserToAddRows = false,
                    AllowUserToDeleteRows = false,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                    SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                    AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.WhiteSmoke }
                };
                grid.Columns.Add("Date", "Date/heure");
                grid.Columns.Add("Duree", "Durée");

                foreach (var entry in entries)
                {
                    var match = Regex.Match(entry, @"^(.*?)\s*\(([^)]*)\)\s*$");
                    if (match.Success)
                    {
                        grid.Rows.Add(match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim());
                    }
                    else
                    {
                        grid.Rows.Add(entry, "");
                    }
                }

                if (grid.Rows.Count == 0)
                {
                    grid.Rows.Add("Aucun historique disponible", "");
                }

                var form = new Form
                {
                    Text = "Historique des connexions",
                    Width = 420,
                    Height = 280,
                    StartPosition = FormStartPosition.CenterScreen
                };
                form.Controls.Add(grid);
                form.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur: {ex.Message}", "Historique des connexions");
            }
        }

        private void ShowConfigDialog()
        {
            var form = new Form
            {
                Text = "Configuration Alcasar Tray",
                Width = 400,
                Height = 300,
                StartPosition = FormStartPosition.CenterScreen
            };

            var label1 = new Label { Text = "URL du portail:", Top = 10, Left = 10, Width = 360 };
            var urlBox = new TextBox { Text = config.PortalUrl, Top = 30, Left = 10, Width = 360 };

            var label2 = new Label { Text = "Nom d'utilisateur:", Top = 60, Left = 10, Width = 360 };
            var userBox = new TextBox { Text = config.Username, Top = 80, Left = 10, Width = 360 };

            var label3 = new Label { Text = "Mot de passe:", Top = 110, Left = 10, Width = 360 };
            var passBox = new TextBox { Text = config.Password, Top = 130, Left = 10, Width = 360, PasswordChar = '*' };

            var label4 = new Label { Text = "Intervalle (secondes):", Top = 160, Left = 10, Width = 360 };
            var intervalBox = new TextBox { Text = config.CheckIntervalSeconds.ToString(), Top = 180, Left = 10, Width = 360 };

            var saveBtn = new Button { Text = "Enregistrer", Top = 210, Left = 10, Width = 100 };
            var cancelBtn = new Button { Text = "Annuler", Top = 210, Left = 120, Width = 100 };

            saveBtn.Click += (_, _) =>
            {
                config.PortalUrl = urlBox.Text;
                config.Username = userBox.Text;
                config.Password = passBox.Text;
                if (int.TryParse(intervalBox.Text, out int interval) && interval > 0)
                {
                    config.CheckIntervalSeconds = interval;
                    timer.Interval = interval * 1000;
                }
                SaveConfig();
                form.Close();
            };

            cancelBtn.Click += (_, _) => form.Close();

            form.Controls.Add(label1);
            form.Controls.Add(urlBox);
            form.Controls.Add(label2);
            form.Controls.Add(userBox);
            form.Controls.Add(label3);
            form.Controls.Add(passBox);
            form.Controls.Add(label4);
            form.Controls.Add(intervalBox);
            form.Controls.Add(saveBtn);
            form.Controls.Add(cancelBtn);

            form.ShowDialog();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer?.Dispose();
                notifyIcon?.Dispose();
                hiddenForm?.Dispose();
                client?.Dispose();
                handler?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public class AlcasarConfig
    {
        public string PortalUrl { get; set; } = "https://alcasar.localdomain/";
        public string Username { get; set; } = "";

        [JsonIgnore]
        public string Password { get; set; } = "";

        // Mot de passe chiffré via DPAPI (Windows), stocké à la place du texte en clair.
        public string EncryptedPassword { get; set; } = "";

        public int CheckIntervalSeconds { get; set; } = 60;
    }
}
