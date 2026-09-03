# ✅ قائمة تحقق النشر — IdentitySyncPro Production Checklist

> **آخر تحديث:** مايو 2026  
> **الحالة:** جاهز للمراجعة

---

## 📋 المرحلة 1: ما قبل النشر (Pre-Deployment)

### 1.1 متطلبات الخادم
- [ ] Windows Server 2019+ مثبت ومحدث
- [ ] .NET 8.0 Runtime مثبت ([تحميل](https://dotnet.microsoft.com/download/dotnet/8.0))
- [ ] SQL Server 2019+ (أو Express) متاح ومتصل
- [ ] Oracle Client (Oracle.ManagedDataAccess) يعمل — أو الوصول للشبكة إلى خادم Oracle
- [ ] الوصول إلى Active Directory عبر LDAP (منفذ 389 أو 636 مع SSL)
- [ ] فتح المنافذ المطلوبة في الـ Firewall:
  - `1521` — Oracle
  - `389` (أو `636` SSL) — Active Directory
  - `1433` — SQL Server
  - `443` — HTTPS (للواجهة)

### 1.2 حسابات الخدمة
- [ ] حساب AD خدمة (Service Account) بصلاحيات:
  - إنشاء مستخدمين (Create Users)
  - تعديل مستخدمين (Modify Users)
  - نقل بين OUs (Move Objects)
  - إضافة لمجموعات (Add to Groups)
- [ ] حساب Oracle للقراءة من `V_STUDENT_DATA`
- [ ] حساب SQL Server (أو Integrated Security)

### 1.3 إعداد Active Directory
- [ ] إنشاء OUs المطلوبة:
  ```
  DC=std,DC=nu,DC=edu,DC=sa
  ├── OU=MALE
  │   ├── OU=NAJRAN
  │   └── OU=SHARORAH
  ├── OU=FEMALE
  │   ├── OU=NAJRAN
  │   └── OU=SHARORAH
  ├── OU=Graduates
  │   ├── OU=MALE
  │   └── OU=FEMALE
  └── OU=LeftTheUniversity
  ```
- [ ] إنشاء المجموعات المطلوبة (حسب قواعد المجموعات المعرّفة في الإعدادات)، مثال:
  - `All-Users-Group` (جميع المستخدمين)
  - مجموعات إضافية حسب المواقع/الفروع إن وجدت

### 1.4 إعداد قاعدة البيانات
- [ ] إنشاء قاعدة بيانات `IdentitySyncProDB` في SQL Server
- [ ] التأكد من صلاحيات `db_owner` لحساب الخدمة
- [ ] التأكد من تفعيل `MultipleActiveResultSets`

---

## 📋 المرحلة 2: إعداد التطبيق (Configuration)

### 2.1 ملف الإعدادات
- [ ] نسخ `appsettings.Production.json` وتعبئة البيانات الفعلية:
  - [ ] `ConnectionStrings:DefaultConnection` — SQL Server
  - [ ] `OracleConnector` — بيانات Oracle الفعلية
  - [ ] `ActiveDirectoryConnector` — بيانات AD الفعلية
  - [ ] `ApiSecurity:ApiKey` — مفتاح API قوي (32+ حرف)
  - [ ] `ApiSecurity:HangfireApiKey` — مفتاح Hangfire قوي

### 2.2 أمان الإنتاج
- [ ] تغيير جميع كلمات المرور الافتراضية
- [ ] تعيين `AllowedHosts` لاسم الخادم الفعلي
- [ ] إعداد شهادة SSL (HTTPS)
- [ ] التأكد من أن `EnableAutoSync = false` (للبدء يدوياً)

---

## 📋 المرحلة 3: النشر (Deployment)

### 3.1 بناء ونشر التطبيق
```powershell
# بناء المشروع
dotnet publish src\IdentitySyncPro.Web -c Release -o C:\inetpub\IdentitySyncPro

# أو استخدام سكربت النشر
.\scripts\deploy.ps1
```

### 3.2 التحقق من قاعدة البيانات
- [ ] التطبيق ينشئ قاعدة البيانات تلقائياً عند أول تشغيل
- [ ] التحقق من أن الـ Migrations طُبقت بنجاح
- [ ] التحقق من أن الـ Seeder أنشأ البيانات الافتراضية:
  - [ ] جهة (Tenant) واحدة على الأقل
  - [ ] 34 ربط حقول
  - [ ] 6 قواعد محرك القواعد
  - [ ] 6 قواعد دورة حياة
  - [ ] 3 قواعد مجموعات
  - [ ] 1 قاعدة OU
  - [ ] إعدادات الاحتفاظ بالبيانات

---

## 📋 المرحلة 4: التحقق (Verification)

### 4.1 التحقق من الاتصالات
- [ ] فتح `/Connector` — اختبار اتصال Oracle ✅
- [ ] فتح `/Connector` — اختبار اتصال AD ✅
- [ ] فتح `/Health` — التحقق من حالة جميع المكونات

### 4.2 التحقق من الإعدادات
- [ ] فتح `/Settings` — التحقق من الجهة والبيانات
- [ ] فتح `/Settings/Mapping/{id}` — التحقق من الـ 34 ربط
- [ ] فتح `/Rules` — التحقق من الـ 6 قواعد
- [ ] فتح `/Lifecycle` — التحقق من الـ 6 قواعد

### 4.3 اختبار المزامنة
- [ ] **تشغيل تجريبي (Dry Run)** أولاً:
  1. افتح `/Sync`
  2. اضغط **"تشغيل تجريبي"**
  3. راجع النتائج — لا يتم أي تغيير فعلي
- [ ] **مزامنة فردية** لطالب واحد:
  1. أدخل رقم طالب معروف
  2. اضغط **"تجريبي"** أولاً
  3. إذا النتيجة صحيحة → اضغط **"مزامنة فعلية"**
- [ ] **مزامنة كاملة** (بعد نجاح الاختبارات):
  1. اضغط **"مزامنة كاملة"**
  2. تابع في `/Sync/Live`

### 4.4 التحقق من Hangfire
- [ ] فتح `/hangfire` — التحقق من تسجيل المهام:
  - [ ] `health-check` — كل 10 دقائق
  - [ ] `data-retention` — كل أحد 3 AM

### 4.5 تفعيل المزامنة التلقائية (عند الجاهزية)
```
1. افتح /Settings
2. عدّل الجهة → EnableAutoSync = true
3. حفظ
```
هذا سيفعّل:
- مزامنة كاملة يومياً الساعة 2 AM
- مزامنة تغييرات كل 30 دقيقة

---

## 📋 المرحلة 5: المراقبة المستمرة (Monitoring)

### 5.1 مراقبة يومية
- [ ] مراجعة `/Dashboard` — البطاقات الإحصائية
- [ ] مراجعة `/Health` — حالة المكونات
- [ ] مراجعة `/Health` — الهويات المحجورة (Quarantine)
- [ ] مراجعة `/Health` — Dead Letter Queue

### 5.2 مراقبة أسبوعية
- [ ] مراجعة `/Reports` — تقارير الأداء
- [ ] مراجعة `/Audit` — سجل التدقيق
- [ ] مراجعة `/hangfire` — المهام الفاشلة
- [ ] مراجعة ملفات Logs في مجلد `Logs/`

### 5.3 النسخ الاحتياطي
- [ ] جدولة نسخ احتياطي يومي لقاعدة بيانات `IdentitySyncProDB`
- [ ] حفظ نسخة من `appsettings.Production.json` في مكان آمن
- [ ] اختبار الاستعادة دورياً

---

## 🚨 خطة الطوارئ (Rollback Plan)

### إذا فشل النظام:
1. **إيقاف التطبيق** فوراً
2. **مراجعة Logs** في `Logs/identitysync-*.log`
3. **التحقق من الاتصالات** (Oracle, AD, SQL)
4. **مراجعة `/Health`** — حالة Circuit Breaker

### إذا تم إنشاء حسابات خاطئة:
> ⛔ النظام **لا يحذف أي حساب** (Safe Sync)
1. الحسابات المنشأة تبقى كما هي
2. يمكن تعديلها يدوياً من AD
3. مراجعة `/Audit` لتتبع العمليات

### إذا فشلت المزامنة:
1. مراجعة `/Sync` — آخر عمليات المزامنة
2. مراجعة التفاصيل لكل عملية فاشلة
3. إعادة تشغيل العمليات الفاشلة من `/Health` → Dead Letter Queue

---

## 🔐 ملاحظات أمنية هامة

| البند | الحالة | ملاحظة |
|:---|:---:|:---|
| Safe Sync (لا حذف/تعطيل) | ✅ مفعّل دائماً | لا يمكن تجاوزه |
| Circuit Breaker | ✅ مفعّل | 3 فشل → توقف 5 دقائق |
| Quarantine | ✅ مفعّل | 3+ فشل → حجر تلقائي |
| API Key | ⚠️ يجب تغييره | قبل النشر |
| HTTPS | ⚠️ يجب إعداده | شهادة SSL مطلوبة |
| AD Service Account | ⚠️ يجب إعداده | صلاحيات محددة فقط |
