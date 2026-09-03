# 📘 دليل استخدام IdentitySyncPro — الشامل

> منصة IAM عربية متكاملة لمزامنة الهويات بين Oracle و Active Directory
> ⛔ **وضع المزامنة الآمنة (Safe Sync)** — النظام لا يحذف ولا يعطل أي حساب أبداً

---

## 📋 فهرس المحتويات

1. [المتطلبات والتشغيل الأول](#1-المتطلبات-والتشغيل-الأول)
2. [البنية المعمارية — المساران](#2-البنية-المعمارية)
3. [لوحة التحكم (Dashboard)](#3-لوحة-التحكم)
4. [الهويات (Identities)](#4-الهويات)
5. [عمليات المزامنة (Sync)](#5-عمليات-المزامنة)
6. [المراقبة المباشرة (Live Monitor)](#6-المراقبة-المباشرة)
7. [الموصّلات (Connectors)](#7-الموصّلات)
8. [محرك القواعد (Rules Engine)](#8-محرك-القواعد)
9. [إدارة دورة الحياة (Lifecycle) — مسار Metaverse](#9-إدارة-دورة-الحياة)
10. [تعطيل وتفعيل الحسابات (Account Status)](#10-تعطيل-وتفعيل-الحسابات)
11. [إدارة الخدمات (Services)](#11-إدارة-الخدمات)
12. [مركز الإشعارات (Notifications Center)](#12-مركز-الإشعارات)
13. [الإعدادات (Settings)](#13-الإعدادات)
14. [ربط الحقول (Attribute Mapping)](#14-ربط-الحقول)
15. [سجل التدقيق (Audit Trail)](#15-سجل-التدقيق)
16. [التقارير (Reports)](#16-التقارير)
17. [صحة النظام (Health)](#17-صحة-النظام)
18. [الاحتفاظ بالبيانات (Data Retention)](#18-الاحتفاظ-بالبيانات)
19. [Hangfire — لوحة المهام](#19-hangfire)
20. [الأمان والحماية](#20-الأمان-والحماية)
21. [التحويلات (Transforms)](#21-التحويلات)
22. [نقل الطلاب وإزالة القروبات](#22-نقل-الطلاب-وإزالة-القروبات)
23. [استكشاف الأخطاء](#23-استكشاف-الأخطاء)
24. [خريطة النظام الكاملة](#24-خريطة-النظام-الكاملة)

---

## 1. المتطلبات والتشغيل الأول

### 1.1 المتطلبات

| المتطلب | الإصدار | الملاحظة |
|:---|:---|:---|
| .NET SDK | 8.0+ | [تحميل](https://dotnet.microsoft.com) |
| SQL Server | 2019+ (أو Express) | لقاعدة بيانات التطبيق |
| Oracle Client | Oracle.ManagedDataAccess | لقراءة بيانات الطلاب |
| Active Directory | Windows Server 2016+ | لإدارة حسابات الطلاب |

### 1.2 التشغيل لأول مرة (خطوة بخطوة)

**الخطوة 1:** استنساخ المشروع
```bash
git clone https://github.com/YOUR_USERNAME/IdentitySyncPro.git
cd IdentitySyncPro
```

**الخطوة 2:** نسخ ملف الإعدادات
```bash
cp src/IdentitySyncPro.Web/appsettings.template.json src/IdentitySyncPro.Web/appsettings.json
```

**الخطوة 3:** تعديل `appsettings.json` — أدخل بيانات الاتصال الحقيقية:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=YOUR_SQL_SERVER;Database=IdentitySyncProDB;Integrated Security=True;TrustServerCertificate=True;MultipleActiveResultSets=true;Connection Timeout=30;Command Timeout=120"
  },
  "OracleConnector": {
    "Host": "192.168.1.100",
    "Port": 1521,
    "ServiceName": "ORCL",
    "UserId": "your_user",
    "Password": "your_pass",
    "ViewName": "V_STUDENT_DATA",
    "CommandTimeout": 300
  },
  "ActiveDirectoryConnector": {
    "Server": "dc01.example.local",
    "Port": 389,
    "UseSsl": false,
    "BaseDN": "DC=example,DC=local",
    "Username": "domain\\admin",
    "Password": "your_pass",
    "DefaultPassword": "ChangeMe@2026"
  },
  "SyncSettings": {
    "DefaultBatchSize": 1000,
    "FullSyncSchedule": "0 2 * * *",
    "DeltaSyncSchedule": "*/30 * * * *",
    "HealthCheckSchedule": "*/10 * * * *",
    "EnableAutoSync": false
  },
  "ApiSecurity": {
    "ApiKey": "GENERATE-A-STRONG-API-KEY-HERE",
    "HangfireApiKey": "GENERATE-A-STRONG-HANGFIRE-KEY-HERE"
  }
}
```

> ⚠️ **هام:** غيّر كل القيم التي تبدأ بـ `YOUR_` و `GENERATE-` قبل التشغيل.

**الخطوة 4:** تشغيل التطبيق
```bash
dotnet run --project src\IdentitySyncPro.Web
```

**الخطوة 5:** فتح المتصفح
```
https://localhost:7286
```

### 1.3 ما يحدث تلقائياً عند أول تشغيل

| الخطوة | ما يحدث | الجدول/المكون |
|:---:|:---|:---|
| 1 | إنشاء قاعدة بيانات `IdentitySyncProDB` | SQL Server |
| 2 | تطبيق EF Core Migrations (إنشاء الجداول الأساسية) | `SyncStates`, `SyncRuns`, `SyncOperations`, `MetaverseEntries`, `MetaverseHistory`, `LifecycleRules`, `SyncRulesV2`, `TenantSettings`, `AuditEntries` |
| 3 | إنشاء جداول Hangfire | `HangFire.*` |
| 4 | إنشاء جداول الخدمات | `Svc_Services`, `Svc_FieldMappings`, `Svc_ExecutionLogs`, `Svc_AuditEntries` |
| 5 | إنشاء جداول مركز الإشعارات | `SmsProviders` |
| 6 | إنشاء جداول تعطيل/تفعيل الحسابات | `Acct_StatusLogs`, `Acct_CustomDomains` |
| 7 | تشغيل ProductionSeeder (بيانات أولية) | إنشاء Tenant افتراضي + قواعد Lifecycle أساسية |
| 8 | تسجيل مهام Hangfire المجدولة | HealthCheck + DataRetention |
| 9 | ضبط اللغة الافتراضية (عربي) | `AppSettings` |

### 1.4 خطوات ما بعد أول تشغيل

بعد فتح المتصفح، اتبع الترتيب التالي:

| الترتيب | الصفحة | ما تفعله |
|:---:|:---|:---|
| 1 | `/Settings` | تأكد من إعدادات الجهة (Tenant) — Oracle + AD + الجدولة |
| 2 | `/SmsCenter` | أضف مزود SMS (اختياري) |
| 3 | `/Connector` | اختبر اتصال Oracle و AD |
| 4 | `/Settings/Mapping/{tenantId}` | اضغط "تحميل الربط الافتراضي" → 34 ربط جاهز |
| 5 | `/Lifecycle` | راجع قواعد دورة الحياة (مثبّتة تلقائياً) |
| 6 | `/Sync` | شغّل **Dry Run** أولاً |
| 7 | `/Sync` | إذا النتائج مقبولة → شغّل **Full Sync** |

### 1.5 النشر للإنتاج (Production Deployment)

استخدم سكربت PowerShell المحدّث:

```powershell
# النشر الأساسي
.\scripts\deploy.ps1

# النشر مع إنشاء Windows Service
.\scripts\deploy.ps1 -CreateWindowsService

# النشر بدون اختبارات ونسخ احتياطي
.\scripts\deploy.ps1 -SkipTests -SkipBackup

# النشر لمسار مخصص
.\scripts\deploy.ps1 -OutputDir "D:\Apps\IdentitySyncPro"
```

**خيارات السكربت:**

| الخيار | الوصف | القيمة الافتراضية |
|:---|:---|:---|
| `-OutputDir` | مسار النشر | `C:\inetpub\IdentitySyncPro` |
| `-Configuration` | نوع البناء | `Release` |
| `-SkipTests` | تخطي الاختبارات | `false` |
| `-SkipBackup` | تخطي النسخ الاحتياطي | `false` |
| `-CreateWindowsService` | إنشاء Windows Service | `false` |

**التشغيل بعد النشر:**
```powershell
cd C:\inetpub\IdentitySyncPro
$env:ASPNETCORE_ENVIRONMENT="Production"
dotnet IdentitySyncPro.Web.dll
```

---

## 2. البنية المعمارية — المساران

النظام يعمل بـ **مسارين مستقلين** لمعالجة الهويات. فهم الفرق بينهما أساسي لاستخدام النظام بشكل صحيح:

### المسار الأول: SyncEngine — المزامنة المباشرة

```
                ┌──────────────────────────────────────────┐
                │         SyncEngine (المسار المباشر)        │
                │                                          │
  Oracle ──────►│  SyncStates ──► ExistsAsync(AD) ──► AD   │
                │  (جدول SQL)     (بحث بـ sAMAccountName)  │
                │                        │                 │
                │                   LifecycleEngine         │
                │              (إذا StatusCode != 1)       │
                │              نقل OU + إزالة قروبات      │
                └──────────────────────────────────────────┘
```

| الخاصية | الوصف |
|:---|:---|
| **يُستخدم في** | صفحة `/Sync` — أزرار المزامنة الكاملة/التغييرات/التجريبية/الفردية |
| **المطابقة** | يبحث مباشرة في AD بـ `sAMAccountName = STUDENT_ID` |
| **إذا وُجد** | يُحدّث بياناته في AD + يفحص StatusCode |
| **إذا لم يوجد** | يُنشئ حساب AD جديد مباشرة |
| **الطلاب غير المنتظمين** | ✅ يُفعّل LifecycleEngine تلقائياً → نقل OU + إزالة قروبات |

### المسار الثاني: LifecycleEngine — عبر Metaverse

```
                ┌──────────────────────────────────────────────────────┐
                │         LifecycleEngine (مسار Metaverse)              │
                │                                                      │
  Oracle ──────►│  Import ──► MetaverseEntries ──► Rules ──► Export ──► AD  │
                │             (مخزن مركزي)    (قواعد الحياة)           │
                │                   │                                  │
                │             MetaverseHistory                         │
                │             (سجل تاريخي)                            │
                └──────────────────────────────────────────────────────┘
```

### متى تستخدم كل مسار:

| الحاجة | المسار | الصفحة |
|:---|:---|:---|
| مزامنة بيانات كل الطلاب (إنشاء/تحديث) | **SyncEngine** | `/Sync` |
| مزامنة طالب واحد سريعاً | **SyncEngine** | `/Sync` (مزامنة فردية) |
| نقل طلاب غير منتظمين + إزالة قروبات | **SyncEngine** ← يُفعّل LifecycleEngine تلقائياً | `/Sync` |
| تتبع دورة حياة الطالب بالتفصيل | **LifecycleEngine** | `/Lifecycle` |
| تطبيق فترة سماح (30 يوم قبل التعليق) | **LifecycleEngine** | `/Lifecycle` |

---

## 3. لوحة التحكم

**المسار:** `/Dashboard` — الصفحة الرئيسية

### البطاقات الإحصائية (4 بطاقات):

| البطاقة | الوصف | مثال |
|:---|:---|:---|
| إجمالي الهويات | عدد السجلات في قاعدة المزامنة | `12,540` |
| الهويات الفعّالة | عدد الحسابات المنشأة في AD | `11,800` |
| العمليات الفاشلة | عدد الهويات في حالة خطأ | `15` |
| الهويات المعلّقة | لم تُنشأ حساباتها بعد | `725` |

### الرسوم البيانية:
- **مخطط 7 أيام**: خط زمني لعمليات الإنشاء والتحديث والفشل يومياً
- **إحصائيات اليوم**: عدد عمليات اليوم الحالي

### آخر 5 عمليات مزامنة + آخر 10 نشاطات

### تبديل اللغة:
- زر **عربي/EN** في الشريط العلوي → يغير كل الواجهة فوراً
- الإعداد يُحفظ في قاعدة البيانات

---

## 4. الهويات

**المسار:** `/Identity`

### عرض الهويات:
جدول يعرض كل الهويات في **قاعدة المزامنة** (SyncStates):

| العمود | الوصف |
|:---|:---|
| رقم الطالب | المعرّف الفريد (STUDENT_ID) |
| الحالة | Synced/Failed/Pending |
| مُنشأ في AD | ✅ أو ❌ |
| آخر مزامنة | تاريخ آخر عملية ناجحة |

### البحث والفلترة:
- بحث بـ **رقم الطالب** أو **نص الحالة**
- فلتر بـ **الحالة** من القائمة المنسدلة

### تفاصيل الهوية (`/Identity/Details/{studentId}`):
- كل بيانات حالة المزامنة
- **آخر 20 عملية** (إنشاء/تحديث/نقل/فشل)

### تصدير CSV:
اضغط **تصدير CSV** → ملف `identities_export.csv`

---

## 5. عمليات المزامنة

**المسار:** `/Sync`

> **⚡ يستخدم المسار المباشر (SyncEngine)** — مع دمج LifecycleEngine لنقل الطلاب غير المنتظمين تلقائياً.

### 5.1 أنواع المزامنة (4 أزرار):

| الزر | الوظيفة | متى تستخدمه |
|:---|:---|:---|
| 🔄 **مزامنة كاملة** | كل الطلاب Oracle → AD | أول مرة أو إعادة شاملة |
| ⚡ **مزامنة تغييرات** | المتغيرون فقط (Delta) | التشغيل اليومي |
| 👁️ **تشغيل تجريبي** | محاكاة بدون تنفيذ فعلي | قبل أي تغيير إنتاجي |
| 📡 **مباشر** | المراقبة اللحظية (SignalR) | أثناء التشغيل |

### 5.2 ماذا يحدث أثناء المزامنة الكاملة:

```
لكل طالب من Oracle:
    ↓
1. هل موجود في AD؟ (ExistsAsync)
    ├── نعم → UpdateAsync (تحديث البيانات)
    └── لا  → CreateAsync (إنشاء حساب جديد + إرسال SMS)
    ↓
2. هل StatusCode = 1 (منتظم)؟
    ├── نعم → ✅ لا شيء إضافي
    └── لا  → LifecycleEngine:
              ├── نقل إلى OU المناسب (Graduates / LeftTheUniversity)
              └── إزالة من جميع القروبات
```

### 5.3 مزامنة فردية:
1. أدخل **رقم الطالب** في الحقل
2. اضغط **Enter** أو **🔄 مزامنة فعلية**
3. النتيجة تظهر فوراً

### 5.4 إلغاء مزامنة:
- اضغط **إلغاء المزامنة** → تتوقف بعد العملية الحالية

### 5.5 جدول العمليات (آخر 20):
- النوع، الحالة، إنشاء/تحديث/نقل/فشل، المدة

---

## 6. المراقبة المباشرة

**المسار:** `/Sync/Live`

- بث مباشر عبر **SignalR**
- شريط تقدم مرئي + نسبة مئوية
- كل عملية تظهر فور اكتمالها
- شارة **🟢 LIVE** أثناء التشغيل

---

## 7. الموصّلات

**المسار:** `/Connector`

### اختبار Oracle:
اضغط **اختبار Oracle** → ✅ `Oracle connection OK (150ms)` أو ❌

### اختبار AD:
اضغط **اختبار AD** → ✅ `AD connection OK (80ms)` أو ❌

### عدد السجلات:
اضغط **عدد سجلات Oracle** → إجمالي الطلاب في الـ View

### مؤشرات الشريط الجانبي:
- 🟢 متصل | 🟡 بطيء | 🔴 منقطع

---

## 8. محرك القواعد (Rules Engine)

**المسار:** `/Rules`

### 8.1 أنواع القواعد:

| النوع | الاتجاه | الوظيفة |
|:---|:---|:---|
| **Join** | Inbound | مطابقة STUDENT_ID بـ sAMAccountName |
| **Projection** | Inbound | إنشاء سجل Metaverse جديد |
| **ImportFlow** | Inbound | نقل attribute من المصدر → Metaverse |
| **ExportFlow** | Outbound | نقل attribute من Metaverse → AD |
| **Provisioning** | Outbound | إنشاء حساب AD جديد |
| **Deprovisioning** | Outbound | ⛔ محمي بـ Safe Sync |

### 8.2 إصدارات القواعد:
كل تعديل يُنشئ **إصدار جديد** تلقائياً — يمكن الرجوع لأي إصدار.

### 8.3 معاينة القاعدة:
أدخل رقم القاعدة + رقم الطالب → يعرض ماذا ستفعل بدون تنفيذ.

---

## 9. إدارة دورة الحياة (Lifecycle)

**المسار:** `/Lifecycle`

> **⚡ يستخدم مسار Metaverse (LifecycleEngine)**

### 9.1 الحالات المدعومة:

```
طالب جديد → Pending → Active ──┬─── تخرّج ──→ Graduated (نقل OU + إزالة قروبات)
                                │
                                ├─── فصل/مطوي قيده ──→ Suspended (نقل OU + إزالة قروبات)
                                │
                                └─── إعادة قبول ──→ Active (مرة أخرى)
```

### 9.2 أنواع الإجراءات (ActionType):

| الإجراء | الوصف | ماذا يحدث في AD |
|:---|:---|:---|
| **SetState** | تغيير حالة الهوية | تحديث MetaverseEntry فقط |
| **MoveOU** | نقل الحساب إلى OU آخر | `ModifyDNRequest` في AD |
| **RemoveGroups** | إزالة من جميع المجموعات | حذف من `member` في كل group |
| **Deprovision** | نقل + إزالة قروبات + تغيير حالة | MoveOU + RemoveGroups + SetState=Suspended |
| **Reactivate** | تفعيل الحساب | SetState=Active + Enable |
| **SendSMS** | إرسال رسالة SMS | — |

### 9.3 مثال: إيقاف طلاب مفصولين:

| الحقل | القيمة |
|:---|:---|
| الاسم | تعليق طالب مفصول |
| حقل الشرط | `STATUS_CODE` |
| العملية | `in` |
| القيمة | `4,5,6` (منقطع، مفصول أكاديمياً، مطوي قيده) |
| الإجراء | `Deprovision` |
| فترة السماح | 14 يوم |

### 9.4 حالات الطلاب وأين يُنقلون:

| StatusCode | الوصف | OU الهدف | إزالة قروبات |
|:---:|:---|:---|:---:|
| 1 | منتظم | يبقى في OU الحالي | ❌ |
| 2 | معتذر | `OU=LeftTheUniversity,{BaseDN}` | ✅ |
| 3 | مؤجل | `OU=LeftTheUniversity,{BaseDN}` | ✅ |
| 4 | منقطع | `OU=LeftTheUniversity,{BaseDN}` | ✅ |
| 5 | مفصول أكاديمياً | `OU=LeftTheUniversity,{BaseDN}` | ✅ |
| 6 | مطوي قيده | `OU=LeftTheUniversity,{BaseDN}` | ✅ |
| 7 | خريج | `OU={GENDER},OU=Graduates,{BaseDN}` | ✅ |
| 9 | مفصول تأديبياً | `OU=LeftTheUniversity,{BaseDN}` | ✅ |
| 10 | منسحب | `OU=LeftTheUniversity,{BaseDN}` | ✅ |
| 11 | متوفى | `OU=LeftTheUniversity,{BaseDN}` | ✅ |
| 12 | مؤجل قبول | `OU=LeftTheUniversity,{BaseDN}` | ✅ |

---

## 10. تعطيل وتفعيل الحسابات (Account Status)

**المسار:** `/AccountStatus`

> **ملاحظة:** هذه الوحدة **مستقلة تماماً** عن IAM والخدمات. تعمل على أي دومين AD.

### 10.1 ما تفعله:
- البحث عن حساب AD بـ sAMAccountName
- تعطيل أو تفعيل الحساب (Toggle)
- إرسال SMS عند التعطيل/التفعيل (اختياري)
- تسجيل كل عملية في سجل تدقيق مستقل

### 10.2 إضافة دومين مخصص:

1. اضغط **"إضافة دومين"** في صفحة Account Status
2. أدخل:

| الحقل | الوصف | مثال |
|:---|:---|:---|
| اسم الدومين | اسم وصفي | `المستخدمون - example.local` |
| عنوان السيرفر | IP أو FQDN | `dc01.example.local` |
| المنفذ | LDAP port | `389` |
| Base DN | نقطة البداية | `DC=example,DC=local` |
| اسم المستخدم | حساب AD | `domain\admin` |
| كلمة المرور | — | — |

3. اضغط **"اختبار الاتصال"** للتأكد
4. اضغط **"حفظ"**

### 10.3 تعطيل/تفعيل حساب:

1. اختر **الدومين** من القائمة
2. أدخل **اسم المستخدم** (sAMAccountName)
3. اضغط **"بحث"** → تظهر بيانات الحساب
4. أدخل **السبب** (مطلوب)
5. اختيارياً: حدد **مزود SMS** + **رقم الجوال** + **قالب الرسالة**
6. اضغط **"تعطيل"** أو **"تفعيل"**

### 10.4 سجل العمليات:
- جدول بكل العمليات مع فلاتر (بحث، إجراء، دومين، تاريخ)
- تصدير إلى **Excel**

---

## 11. إدارة الخدمات (Services)

**المسار:** `/Services`

> **ملاحظة:** وحدة الخدمات **مستقلة تماماً** عن IAM. لها جداول منفصلة (`Svc_`) وإعدادات اتصال خاصة لكل خدمة.

### 11.1 نوعان من الخدمات:

| النوع | الوظيفة | مثال |
|:---|:---|:---|
| **🔄 مزامنة (Sync)** | مزامنة حقول DB → AD | تحديث بيانات الموظفين |
| **🚫 إخلاء طرف (Offboarding)** | تعطيل حسابات + نقل OU | إخلاء طرف الموظفين |

### 11.2 إنشاء خدمة (معالج 4 خطوات):

**الخطوة 1: معلومات الخدمة**
| الحقل | الوصف |
|:---|:---|
| اسم الخدمة | اسم وصفي (مطلوب) |
| نوع الخدمة | Sync أو Offboarding |
| عمود الحالة | (لإخلاء الطرف) العمود الذي يحدد حالة الموظف |
| قيمة غير الفعّال | (لإخلاء الطرف) مثل `Inactive` |
| OU المستهدف | (لإخلاء الطرف) OU النقل بعد التعطيل |
| مزود SMS | اختر من مركز الإشعارات |

**الخطوة 2: قاعدة البيانات المصدر**
| الحقل | الوصف |
|:---|:---|
| نوع DB | SQL Server أو Oracle |
| عنوان الخادم | IP أو اسم الخادم |
| اسم الفيو/الجدول | المصدر (مثل `V_EMPLOYEES`) |
| عمود المفتاح | العمود للبحث في AD |
| خاصية البحث في AD | مثل `extensionAttribute2` |

**الخطوة 3: Active Directory**
| الحقل | الوصف |
|:---|:---|
| عنوان خادم AD | FQDN أو IP |
| المنفذ | 389 أو 636 (SSL) |
| Base DN | نقطة البداية |

**الخطوة 4: الجدولة**
| نوع | مثال |
|:---|:---|
| يومي | الساعة 02:00 |
| أسبوعي | الأحد والأربعاء |
| فترة زمنية | كل 60 دقيقة |
| Cron مخصص | `0 */2 * * *` |

### 11.3 ربط الحقول (نوع Sync فقط):
- أضف صفوف: عمود المصدر → خاصية AD
- سحب الصفوف لتغيير الترتيب (Drag & Drop)

### 11.4 سجلات التشغيل (`/Services/Logs/{id}`):
آخر 50 عملية مع: وقت البدء، المدة، الحالة، معالَج/ناجح/فشل/تخطي

### 11.5 سجل التدقيق (`/Services/AuditLog/{id}`):
تفصيل كل عملية فردية مع فلترة بنوع الإجراء

---

## 12. مركز الإشعارات (Notifications Center)

**المسار:** `/SmsCenter`

> مركز مركزي لإدارة مزودي خدمة الرسائل النصية (SMS). يمكن ربط المزود بأي وحدة في النظام.

### 12.1 ما هو:
بدلاً من إدخال بيانات SMS في كل مكان (الإعدادات، الخدمات، Account Status)، يمكنك إنشاء مزود SMS مرة واحدة واستخدامه في كل الوحدات.

### 12.2 إضافة مزود SMS:

1. اذهب إلى `/SmsCenter`
2. اضغط **"إضافة مزود"**
3. أدخل:

| الحقل | الوصف | مثال |
|:---|:---|:---|
| **اسم المزود** | اسم وصفي (مطلوب) | `مزود SMS الرئيسي` |
| **رابط API** | عنوان API (مطلوب) | `https://api.sms.provider.com/send` |
| **اسم المستخدم** | — | `sms_user` |
| **كلمة المرور** | — | `sms_pass` |
| **اسم المرسل** | — | `University` |
| **ملاحظات** | اختياري | `العقد ينتهي 2027` |

4. اضغط **"حفظ"**

### 12.3 اختبار المزود:

1. في صفحة المزود، أدخل **رقم جوال** ورسالة اختبار
2. اضغط **"إرسال اختبار"**
3. النتيجة: ✅ تم الإرسال أو ❌ خطأ + تفاصيل

### 12.4 ربط المزود بالوحدات:

| الوحدة | أين تربطه |
|:---|:---|
| **الإعدادات (Tenant)** | `/Settings` → قسم SMS → اختر المزود من القائمة |
| **الخدمات** | `/Services/Edit/{id}` → قسم SMS → اختر المزود |
| **تعطيل/تفعيل الحسابات** | `/AccountStatus` → عند التعطيل → اختر المزود |

### 12.5 تفعيل/تعطيل المزود:
- اضغط زر التبديل على بطاقة المزود
- المزود المعطّل لا يظهر في قوائم الاختيار

### 12.6 حذف المزود:
- لا يمكن حذف مزود مرتبط بجهة (Tenant)
- يظهر تحذير: "لا يمكن حذف — مستخدم من قبل جهة واحدة على الأقل"

---

## 13. الإعدادات

**المسار:** `/Settings`

### 13.1 إدارة الجهات (Multi-Tenant):

| القسم | الحقول |
|:---|:---|
| **معلومات الجهة** | الاسم، الوصف، مفعّلة/معطّلة |
| **مصدر البيانات** | المزود (Oracle/SQL)، الخادم، المنفذ، القاعدة، الـ View |
| **Active Directory** | الخادم، المنفذ، SSL، BaseDN، كلمة مرور افتراضية |
| **قاعدة بيانات التطبيق** | المزود، الخادم، المنفذ، اسم القاعدة |
| **الجدولة** | Full Sync، Delta Sync، Health Check |
| **SMS** | تفعيل/تعطيل، **اختيار مزود من مركز الإشعارات** |

### 13.2 ربط المزود بالجهة:
في قسم SMS بالإعدادات:
- اختر المزود من القائمة المنسدلة (يعرض المزودات الفعّالة من مركز الإشعارات)
- أو أدخل إعدادات SMS يدوياً (طريقة قديمة)

---

## 14. ربط الحقول (Attribute Mapping)

**المسار:** `/Settings/Mapping/{tenantId}`

### 14.1 الأقسام الثلاثة:

#### ربط الحقول:
- **تحميل الربط الافتراضي** → 34 ربط جاهز يطابق سكربت PowerShell الأصلي
- حقل AD مفتوح — يقبل أي attribute حتى لو غير في القائمة
- يدعم Attributes متعددة القيم (مثل `proxyAddresses`)

#### المجموعات (Groups):
| الحقل | مثال |
|:---|:---|
| اسم المجموعة | `Site1-Users-Group` |
| افتراضي | ✅ = كل المستخدمين |
| شرط | `CITY_NO == 1` |

#### قواعد OU:
| الحقل | مثال |
|:---|:---|
| قالب OU | `OU={GENDER},OU={CITY},{BaseDN}` |
| خريطة القيم | `{"CITY":{"14":"NAJRAN"},"GENDER":{"1":"MALE"}}` |

---

## 15. سجل التدقيق

**المسار:** `/Audit`

- سجل كامل لكل عملية (إنشاء/تحديث/نقل/إزالة قروبات/فشل)
- فلاتر: الفئة، الخطورة، CorrelationId
- 30 سجل لكل صفحة مع ترقيم

---

## 16. التقارير

**المسار:** `/Reports`

- 4 بطاقات إحصائية (إجمالي التشغيل/ناجح/فاشل/فردي)
- مخطط 30 يوم: اتجاهات الإنشاء/التحديث/الفشل
- توزيع الحالات (مخطط دائري)
- أكثر 10 أخطاء شيوعاً

---

## 17. صحة النظام

**المسار:** `/Health`

### مكونات المراقبة:
| المكون | ما يُراقب |
|:---|:---|
| Oracle | حالة الاتصال + Circuit Breaker |
| Active Directory | حالة الاتصال + Circuit Breaker |
| SQL Server | حالة قاعدة البيانات |

### Quarantine (الحجر):
- هويات فشلت 3+ مرات → تُحجر تلقائياً
- حل: اضغط ✅ **حل** → تُعالج في المزامنة القادمة

### Dead Letter Queue:
- عمليات فشلت نهائياً
- إعادة تشغيل فردياً أو جماعياً

---

## 18. الاحتفاظ بالبيانات

**المسار:** `/Settings` (القسم السفلي)

| نوع البيانات | الافتراضي |
|:---|:---:|
| عمليات المزامنة | 90 يوم |
| سجلات التشغيل | 180 يوم |
| سجلات التدقيق | 365 يوم |
| العمليات الفاشلة | 30 يوم |
| الهويات المحجورة | 60 يوم |

- **Hangfire Job** أسبوعياً (أحد 3 صباحاً)
- ⛔ لا يمس بيانات الطلاب أو حسابات AD

---

## 19. Hangfire

**المسار:** `/hangfire` (يفتح في تبويب جديد)

### المهام المجدولة:

| المهمة | الجدول | القائمة |
|:---|:---|:---|
| Full Sync | يومياً 2:00 AM | sync |
| Delta Sync | كل 30 دقيقة | sync |
| Health Check | كل 10 دقائق | default |
| Data Retention | أسبوعياً (أحد 3 AM) | maintenance |
| خدمات Services | حسب إعدادات كل خدمة | services |

> **ملاحظة:** المزامنة التلقائية معطّلة افتراضياً. لتفعيلها: غيّر `EnableAutoSync` إلى `true` في `appsettings.json`.

---

## 20. الأمان والحماية

### ⛔ Safe Sync (المسار المباشر):
| ✅ مسموح | ❌ ممنوع |
|:---|:---|
| إنشاء حساب AD جديد | حذف حساب AD |
| تحديث بيانات | تعطيل حساب AD |
| نقل بين OUs | إعادة تسمية |
| إضافة لمجموعات | — |
| **إزالة من قروبات** (للطلاب غير المنتظمين) | — |

> **استثناء:** خدمة إخلاء الطرف وصفحة Account Status يمكنهم تعطيل الحسابات.

### Circuit Breaker:
- 3 فشل متتالي → توقف 5 دقائق → يعود تلقائياً

### API Security:
- مفاتيح في `appsettings.json` → `ApiSecurity`
- **غيّر المفاتيح الافتراضية في الإنتاج!**

---

## 21. التحويلات (Transforms)

تُستخدم في عمود **"تحويل"** في صفحة Mapping:

| التحويل | الصيغة | المثال | النتيجة |
|:---|:---|:---|:---|
| **Format** | `Format:{0}@domain` | `Format:{0}@example.com` | `12345@example.com` |
| **Concat** | `Concat:{F1} {F2}` | `Concat:{FIRST} {LAST}` | `Ahmed Ali` |
| **Map** | `Map:K1=V1,K2=V2` | `Map:1=MALE,2=FEMALE` | `MALE` |
| **Static** | `Static:VALUE` | `Static:User` | `User` دائماً |
| **Truncate** | `Truncate:N` | `Truncate:4` | أول 4 أحرف |
| **GetInitials** | `GetInitials` | — | > 4 أحرف = أول حرف فقط |
| **ToUpper** | `ToUpper` | — | `ahmed` → `AHMED` |
| **ToLower** | `ToLower` | — | `AHMED` → `ahmed` |
| **Trim** | `Trim` | — | إزالة المسافات |

---

## 22. نقل الطلاب وإزالة القروبات

> **تصميم قائم على القواعد**: نقل OU وإزالة القروبات يعتمدان **100% على قواعد دورة الحياة**. إذا لا توجد قاعدة = لا يحدث شيء.

### كيف يعمل:

1. أثناء `SyncEngine.RunFullSyncAsync`، بعد تحديث بيانات الطالب في AD
2. يفحص `StudentStatusHelper.IsActiveStudent(StatusCode)`
3. إذا الطالب **غير منتظم** (StatusCode ≠ 1):
   - يستدعي `LifecycleEngine.ProcessIdentityAsync`
   - LifecycleEngine يبحث عن **قواعد مطابقة** في جدول `LifecycleRules`
   - يُنفّذ فقط القواعد التي شروطها تتحقق

### التحكم الكامل عبر القواعد:

| ماذا تريد | القاعدة المطلوبة | ActionType |
|:---|:---|:---|
| نقل الطلاب غير المنتظمين إلى OU آخر | أنشئ قاعدة بشرط StatusCode | `MoveOU` |
| إزالة الطلاب من جميع القروبات | أنشئ قاعدة بشرط StatusCode | `RemoveGroups` |
| نقل + إزالة قروبات معاً | أنشئ قاعدة بشرط StatusCode | `Deprovision` |
| **عدم إزالة أي طالب من القروبات** | **لا تنشئ قاعدة RemoveGroups** | — |

### مثال: إعداد القواعد المطلوبة

**قاعدة 1 — نقل OU (لجميع غير المنتظمين):**

| الحقل | القيمة |
|:---|:---|
| الاسم | `نقل غير المنتظمين` |
| حقل الشرط | `STATUS_CODE` |
| العملية | `not_in` |
| القيمة | `1` |
| الإجراء | `MoveOU` |
| قيمة الإجراء | `OU=LeftTheUniversity,{BaseDN}` |

**قاعدة 2 — إزالة القروبات (اختيارية):**

| الحقل | القيمة |
|:---|:---|
| الاسم | `إزالة القروبات` |
| حقل الشرط | `STATUS_CODE` |
| العملية | `not_in` |
| القيمة | `1` |
| الإجراء | `RemoveGroups` |

> ⚠️ **إذا حذفت قاعدة "إزالة القروبات" → لن يُزال أي طالب من أي قروب.**
> ✅ **إذا أضفتها → يُزال كل طالب غير منتظم من جميع القروبات تلقائياً.**

### القروبات التي تُزال (عند وجود قاعدة RemoveGroups):
- كل مجموعة في `memberOf` attribute
- **ما عدا** Primary Group (عادة `Domain Users`) — AD لا يسمح بإزالتها

### من يشمله هذا:
| StatusCode | الوصف | نقل OU | إزالة قروبات |
|:---:|:---|:---:|:---:|
| 1 | منتظم | ❌ | ❌ |
| 2-6, 9-12 | معتذر/مؤجل/منقطع/مفصول/مطوي/منسحب/متوفى | ✅ (إذا وُجدت قاعدة MoveOU) | ✅ (إذا وُجدت قاعدة RemoveGroups) |
| 7 | خريج | ✅ (إذا وُجدت قاعدة MoveOU) | ✅ (إذا وُجدت قاعدة RemoveGroups) |

### مراقبة العمليات:
- العمليات تُسجل في `SyncOperations` بنوع `Move`
- يمكن مراقبتها من `/Sync/Details/{runId}`
- السجل يعرض: "Moved to OU=LeftTheUniversity,... | Removed from 3 groups: GroupA, GroupB, GroupC"

---

## 23. استكشاف الأخطاء

### المشكلة: Oracle لا يستجيب
- **الأعراض:** Dashboard يظهر `Oracle: Unhealthy`
- **الخطوات:** تحقق `/Health` → Circuit Breaker يحمي تلقائياً → يعود خلال 5 دقائق

### المشكلة: AD لا يستجيب
```powershell
Test-NetConnection DC01 -Port 389  # تحقق من الاتصال
```

### المشكلة: طالب لم يُنشأ حسابه
1. تحقق من Oracle: `SELECT * FROM V_STUDENT_DATA WHERE STUDENT_ID = 'XXX'`
2. تحقق من DLQ في `/Health`
3. تحقق من Quarantine في `/Health`
4. استخدم **مزامنة فردية** في `/Sync`

### المشكلة: طالب لم يُنقل إلى OU المناسب
1. تحقق من `StatusCode` في Oracle — هل تغيّر فعلاً؟
2. شغّل **مزامنة فردية** لهذا الطالب
3. تحقق من سجلات العمليات بنوع `Move`

### المشكلة: القروبات لم تُزل
1. تأكد أن الطالب غير منتظم (StatusCode ≠ 1)
2. شغّل مزامنة فردية
3. تحقق من الـ Logs: `Select-String -Path "Logs\identitysync-*.log" -Pattern "RemoveFromAllGroups"`

### المشكلة: خدمة إخلاء الطرف لا تعمل
1. تحقق أن الخدمة **مفعّلة** في `/Services`
2. تحقق من **اتصال DB وAD** في صفحة التعديل
3. راجع **السجلات** `/Services/Logs/{id}`

### المشكلة: SMS لا يُرسل
1. تحقق من **مركز الإشعارات** `/SmsCenter` — هل المزود فعّال؟
2. اختبر المزود بإرسال رسالة اختبار
3. تحقق أن المزود مربوط بالجهة في `/Settings`

### البحث في الـ Logs:
```powershell
# البحث عن أخطاء
Select-String -Path "Logs\identitysync-*.log" -Pattern "ERROR"

# البحث بـ CorrelationId
Select-String -Path "Logs\identitysync-*.log" -Pattern "abc-123-def"

# البحث عن عمليات النقل
Select-String -Path "Logs\identitysync-*.log" -Pattern "MoveToOU|RemoveFromAllGroups"

# البحث عن عمليات إخلاء الطرف
Select-String -Path "Logs\identitysync-*.log" -Pattern "SvcOffboarding"
```

---

## 24. خريطة النظام الكاملة

### الصفحات:

```
/Dashboard ──────── لوحة التحكم الرئيسية
/Identity ─────────  عرض وبحث الهويات + تصدير CSV
/Sync ──────────────  عمليات المزامنة (Full/Delta/DryRun/Single)  ← SyncEngine
/Sync/Live ────────  المراقبة المباشرة (SignalR)
/Connector ────────  حالة الموصّلات (Oracle/AD) + اختبار
/Rules ──────────── محرك القواعد (Join/Import/Export/Provision)
/Lifecycle ────────  إدارة دورة حياة الهوية                     ← LifecycleEngine + Metaverse
/AccountStatus ────  تعطيل وتفعيل الحسابات (مستقل)              ← AccountStatusService
/Services ─────────  إدارة الخدمات (Sync + Offboarding)         ← SvcSyncExecutor/SvcOffboardingExecutor
/Services/Edit ────  تعديل خدمة + ربط الحقول
/Services/Logs ────  سجلات تشغيل الخدمة
/Services/AuditLog  سجل تدقيق تفصيلي للخدمة
/SmsCenter ────────  مركز الإشعارات (مزودي SMS)                 ← SmsCenterController
/SmsCenter/Create ─  إضافة مزود SMS جديد
/SmsCenter/Edit ───  تعديل مزود SMS
/Settings ─────────  إعدادات الجهات + الاحتفاظ بالبيانات
/Settings/Mapping ─ ربط الحقول + المجموعات + OU
/Audit ──────────── سجل التدقيق العام
/Reports ──────────  التقارير والإحصائيات
/Health ──────────── صحة النظام + Quarantine + DLQ
/hangfire ──────────  لوحة المهام المجدولة (Hangfire)
```

### البنية المعمارية الكاملة:

```
┌─────────────────────────────────────────────────────────────────────┐
│                        IdentitySyncPro                              │
│                                                                     │
│  المسار 1: SyncEngine (المزامنة المباشرة) ← /Sync                  │
│  ┌─────────────────────────────────────────────────────┐            │
│  │  Oracle ──► SyncStates ──► AD (sAMAccountName)      │            │
│  │                 ↕              ↓                    │            │
│  │  SyncRuns ← SyncOperations   LifecycleEngine        │            │
│  │                              (نقل OU + إزالة قروب) │            │
│  └─────────────────────────────────────────────────────┘            │
│                                                                     │
│  المسار 2: LifecycleEngine (عبر Metaverse) ← /Lifecycle            │
│  ┌─────────────────────────────────────────────────────┐            │
│  │  Oracle ──► MetaverseEntries ──► LifecycleRules     │            │
│  │                    ↓                    ↓            │            │
│  │            MetaverseHistory      Export ──► AD       │            │
│  │                                (MoveOU + RemoveGrp) │            │
│  └─────────────────────────────────────────────────────┘            │
│                                                                     │
│  الخدمات: SvcService (مستقلة) ← /Services                         │
│  ┌─────────────────────────────────────────────────────┐            │
│  │  DB مصدر ──► Svc_AuditEntries ──► AD               │            │
│  │                    ↕                                 │            │
│  │  Svc_ExecutionLogs (سجل التشغيل)                   │            │
│  └─────────────────────────────────────────────────────┘            │
│                                                                     │
│  تعطيل/تفعيل: AccountStatus (مستقل) ← /AccountStatus              │
│  ┌─────────────────────────────────────────────────────┐            │
│  │  Custom Domains ──► AD (Disable/Enable)             │            │
│  │  Acct_StatusLogs (سجل تدقيق)                       │            │
│  └─────────────────────────────────────────────────────┘            │
│                                                                     │
│  مركز الإشعارات: SmsCenter ← /SmsCenter                           │
│  ┌─────────────────────────────────────────────────────┐            │
│  │  SmsProviders ──► مشترك بين كل الوحدات              │            │
│  │  (Tenant + Services + AccountStatus)                │            │
│  └─────────────────────────────────────────────────────┘            │
└─────────────────────────────────────────────────────────────────────┘
```

### الجداول الكاملة:

| الوحدة | الجداول |
|:---|:---|
| **IAM الأساسية** | `SyncStates`, `SyncRuns`, `SyncOperations`, `MetaverseEntries`, `MetaverseHistory`, `LifecycleRules`, `SyncRulesV2`, `TenantSettings`, `AuditEntries`, `AppSettings`, `AttributeMappings`, `GroupMappings`, `OURules` |
| **مركز الإشعارات** | `SmsProviders` |
| **الخدمات** | `Svc_Services`, `Svc_FieldMappings`, `Svc_ExecutionLogs`, `Svc_AuditEntries` |
| **تعطيل/تفعيل** | `Acct_StatusLogs`, `Acct_CustomDomains` |
| **Hangfire** | `HangFire.Job`, `HangFire.RecurringJob`, `HangFire.State`, `HangFire.Set`, `HangFire.Hash` |
| **صحة النظام** | `QuarantinedIdentities`, `DeadLetterQueue` |

---

*آخر تحديث: يونيو 2026 · الإصدار: 3.2*
