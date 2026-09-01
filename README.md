# AlcasarTray

Application Windows 11 pour maintenir une connexion Alcasar via une icône dans la zone de notification (systray).

## 🎯 Fonctionnalités

- ✅ Icône dans la barre de notification
- ✅ Vérification périodique de la connexion
- ✅ Reconnexion automatique
- ✅ Menu contextuel pour contrôler l'application
- ✅ Configuration persistante (URL, identifiants, intervalle)
- ✅ Gestion des cookies HTTP

## 🔧 Prérequis

- .NET 8 SDK
- Windows 10/11

## 📦 Installation

```bash
# Cloner le repo
git clone https://github.com/yourusername/AlcasarTray.git
cd AlcasarTray

# Restaurer les dépendances
dotnet restore

# Compiler
dotnet build

# Exécuter
dotnet run
```

## ⚙️ Configuration

L'application crée un fichier `config.json` dans :
- `%AppData%\AlcasarTray\config.json`

Vous pouvez configurer :
- **PortalUrl**: URL du portail Alcasar
- **Username**: Nom d'utilisateur
- **Password**: Mot de passe
- **CheckIntervalSeconds**: Intervalle de vérification en secondes (défaut: 60)

## 🖱️ Utilisation

1. Double-clic sur l'icône → Vérification manuelle
2. Menu contextuel → Reconnecter, Configurer, État
3. L'application se charge au lancement de Windows

## 🏗️ Architecture

```
AlcasarTray/
├── Program.cs              # Point d'entrée
├── AlcasarTrayApp.cs       # Classe principale de l'application
├── AlcasarTray.csproj      # Configuration projet
└── config.json             # Configuration (généré à la première exécution)
```

## 📝 Notes

- Les certificats SSL non valides sont acceptés (développement)
- Les identifiants sont stockés localement en clair (à sécuriser en production)
- La connexion utilise des cookies HTTP standards

## 🚀 Améliorations futures

- Chiffrement des identifiants
- Notification système
- Historique des connexions
- Mode développement pour debug
