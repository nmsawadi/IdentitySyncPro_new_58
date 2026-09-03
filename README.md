# 🔄 IdentitySyncPro

**Enterprise Identity Synchronization Platform** — A comprehensive IAM (Identity and Access Management) solution for synchronizing identities between Oracle databases and Active Directory.

> Built with ASP.NET Core 8.0 | SQL Server | Hangfire | SignalR

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## ✨ Features

- **🔄 Full & Delta Sync** — Synchronize all identities or only changed records
- **👁️ Dry Run Mode** — Preview changes before applying them
- **📡 Live Monitoring** — Real-time sync progress via SignalR
- **🛡️ Safe Sync** — Never deletes or disables AD accounts (safety-first approach)
- **📊 Dashboard** — Rich analytics with charts and statistics
- **🔧 Rules Engine** — Flexible FIM/MIM-style rules for data flow control
- **🔄 Lifecycle Management** — Automated state transitions with grace periods
- **🗺️ Attribute Mapping** — 34 pre-configured mappings with custom transforms
- **🏥 Health Monitoring** — Circuit breaker, quarantine, and dead letter queue
- **📋 Audit Trail** — Complete audit log with correlation IDs
- **⏰ Scheduled Jobs** — Hangfire-powered background tasks
- **🌐 Bilingual UI** — Arabic & English interface
- **🏢 Multi-Tenant** — Support for multiple organizations

## 🏗️ Architecture

```
┌─────────────┐     ┌──────────────┐     ┌──────────────────┐
│   Oracle DB  │────▶│  Metaverse   │────▶│ Active Directory │
│  (Source)    │     │  (Staging)   │     │    (Target)      │
└─────────────┘     └──────────────┘     └──────────────────┘
                           │
                    ┌──────┴──────┐
                    │  Rules      │
                    │  Engine     │
                    └─────────────┘
```

### Project Structure

```
IdentitySyncPro/
├── src/
│   ├── IdentitySyncPro.Core/           # Domain models, interfaces, enums
│   ├── IdentitySyncPro.Infrastructure/ # Data access, connectors, services
│   ├── IdentitySyncPro.Web/            # ASP.NET Core MVC web application
│   └── IdentitySyncPro.Tests/          # Unit & integration tests
├── docs/                               # Documentation
├── migrations/                         # Database migration scripts
└── IdentitySyncPro.sln                 # Solution file
```

## 🚀 Getting Started

### Prerequisites

| Requirement | Version | Notes |
|:---|:---|:---|
| .NET SDK | 8.0+ | [Download](https://dotnet.microsoft.com) |
| SQL Server | 2019+ (or Express) | Application database |
| Oracle Client | Oracle.ManagedDataAccess | Source data provider |
| Active Directory | Windows Server 2016+ | Target directory |

### Installation

1. **Clone the repository**
   ```bash
   git clone https://github.com/YOUR_USERNAME/IdentitySyncPro.git
   cd IdentitySyncPro
   ```

2. **Configure settings**
   ```bash
   # Copy the template and fill in your values
   cp src/IdentitySyncPro.Web/appsettings.template.json src/IdentitySyncPro.Web/appsettings.json
   ```
   
   Update the following in `appsettings.json`:
   - `ConnectionStrings:DefaultConnection` — Your SQL Server connection
   - `OracleConnector` — Oracle database settings
   - `ActiveDirectoryConnector` — AD server details
   - `ApiSecurity` — Change default API keys

3. **Run the application**
   ```bash
   dotnet run --project src/IdentitySyncPro.Web
   ```

4. **Open in browser**
   ```
   https://localhost:7286
   ```

### First Run

On first launch, the application will automatically:
- Create the `IdentitySyncProDB` database
- Apply EF Core migrations
- Set up Hangfire for scheduled tasks
- Configure default language (Arabic)

## 📖 Documentation

See the [User Guide](docs/USER_GUIDE.md) for comprehensive documentation covering:

- Dashboard & Analytics
- Identity Management
- Sync Operations (Full, Delta, Dry Run)
- Live Monitoring (SignalR)
- Connectors Configuration
- Rules Engine
- Lifecycle Management
- Attribute Mapping & Transforms
- Audit Trail & Reports
- Health Monitoring
- Data Retention Policies

## 🔧 Configuration

### Sync Schedules (Hangfire)

| Job | Default Schedule | Queue |
|:---|:---|:---|
| Full Sync | Daily at 2:00 AM | default |
| Delta Sync | Every 30 minutes | default |
| Health Check | Every 10 minutes | health |
| Data Retention | Weekly (Sunday 3 AM) | maintenance |

### Available Transforms

| Transform | Syntax | Example |
|:---|:---|:---|
| Format | `Format:{0}@domain` | `Format:{0}@example.com` |
| Concat | `Concat:{F1} {F2}` | `Concat:{FIRST} {LAST}` |
| Map | `Map:K1=V1,K2=V2` | `Map:1=MALE,2=FEMALE` |
| Static | `Static:VALUE` | `Static:User` |
| Truncate | `Truncate:N` | `Truncate:4` |
| GetInitials | `GetInitials` | First char if > 4 chars |
| ToUpper / ToLower | `ToUpper` | Case conversion |
| Trim | `Trim` | Remove whitespace |

## 🛡️ Security

### Safe Sync Policy

| ✅ Allowed | ❌ Blocked |
|:---|:---|
| Create new AD account | Delete AD account |
| Update account attributes | Disable AD account |
| Move between OUs | Rename account |
| Add to groups | Remove from all groups |

### Protection Mechanisms

- **Circuit Breaker** — Stops after 3 consecutive failures, auto-recovers after 5 minutes
- **Quarantine** — Identities failing 3+ times are quarantined until manually resolved
- **Dead Letter Queue** — Failed operations can be retried individually or in bulk

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the project
2. Create your feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 👤 Author

**Nasser Mahdi Sawadi**

---

> ⚠️ **Note:** This project was designed for educational institution identity management. Modify the configurations and mappings according to your organization's needs.
