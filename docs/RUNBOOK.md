# 🛠️ IdentitySyncPro — Operational Runbook

> دليل التشغيل والاستجابة للحوادث — للمشغّل الذي لم يكتب الكود
> ⛔ **النظام لا يحذف ولا يعطل أي حساب AD أبداً (Safe Sync)**

---

## 📋 معلومات سريعة

| البند | القيمة |
|:---|:---|
| **URL** | `https://localhost:7286` (أو حسب البيئة) |
| **Hangfire** | `/hangfire` |
| **Health** | `/Health` |
| **Logs (نصية)** | `Logs/identitysync-*.log` |
| **Logs (JSON)** | `Logs/identitysync-json-*.log` |
| **قاعدة البيانات** | `IdentitySyncProDB` (SQL Server) |
| **المهام المجدولة** | Full Sync (2 AM) · Delta Sync (كل 30 دقيقة) · Health (كل 10 دقائق) · Retention (أحد 3 AM) |

---

## 🚨 السيناريو 1: توقف Oracle عن الاستجابة

### الأعراض:
- Dashboard يظهر `Oracle: Unhealthy`
- الـ Logs تحتوي: `Oracle health check FAILED`
- Circuit Breaker ينفتح بعد 3 محاولات فاشلة

### التأثير:
- **المزامنة تتوقف تلقائياً** (Circuit Breaker يحمي النظام)
- **حسابات AD لا تتأثر** — لا حذف ولا تعطيل
- الطلاب الحاليون يستمرون بالعمل بشكل طبيعي

### الإجراء:
1. **لا تفعل شيئاً عاجلاً** — النظام يحمي نفسه تلقائياً
2. تواصل مع فريق Oracle/DBA لإصلاح المشكلة
3. تحقق من الاتصال:
   ```sql
   -- من SQL Server Management Studio
   SELECT * FROM HangFire.Job WHERE StateName = 'Failed' ORDER BY CreatedAt DESC
   ```
4. بعد عودة Oracle → Circuit Breaker يُغلق تلقائياً خلال 5 دقائق
5. شغّل Full Sync يدوياً من Dashboard إذا أردت تسريع العملية

### الوقت المتوقع للعودة: فوري بعد عودة Oracle

---

## 🚨 السيناريو 2: توقف Active Directory عن الاستجابة

### الأعراض:
- Dashboard يظهر `AD: Unhealthy`
- عمليات المزامنة تفشل مع `LdapException`
- الطلاب الجدد لا يُنشأون

### التأثير:
- **الحسابات الحالية لا تتأثر** — لا حذف ولا تعطيل
- العمليات الفاشلة تذهب إلى **Dead Letter Queue**
- يمكن إعادة تشغيلها لاحقاً

### الإجراء:
1. تحقق من AD Server: `Test-Connection DC01`
2. تحقق من LDAP: `Test-NetConnection DC01 -Port 389`
3. تحقق من الحساب المستخدم للاتصال (قد تكون كلمة المرور انتهت)
4. بعد العودة → اذهب إلى `/Health` → اضغط **"إعادة تشغيل الكل"**
5. راقب النتائج في الـ Logs

---

## 🚨 السيناريو 3: قاعدة بيانات SQL Server ممتلئة أو بطيئة

### الأعراض:
- صفحات الموقع بطيئة جداً
- أخطاء `SqlException: Timeout`
- Hangfire Jobs تفشل

### الإجراء:
1. **تحقق من حجم القاعدة:**
   ```sql
   EXEC sp_spaceused
   SELECT name, size * 8 / 1024 AS SizeMB FROM sys.database_files
   ```
2. **شغّل Data Retention يدوياً** من Hangfire Dashboard:
   - اذهب إلى `/hangfire/recurring`
   - اضغط "Trigger now" على `data-retention`
3. **أو نظّف يدوياً:**
   ```sql
   -- حذف عمليات مزامنة أقدم من 60 يوم
   DELETE FROM SyncOperations WHERE Timestamp < DATEADD(DAY, -60, GETUTCDATE())
   
   -- حذف سجلات تدقيق أقدم من 6 أشهر
   DELETE FROM AuditEntries WHERE Timestamp < DATEADD(MONTH, -6, GETUTCDATE())
   
   -- تقليص القاعدة
   DBCC SHRINKDATABASE(IdentitySyncProDB)
   ```
4. **راجع إعدادات Retention** في `/Settings` (قسم الاحتفاظ بالبيانات)

---

## 🚨 السيناريو 4: عمليات فاشلة كثيرة (DLQ ممتلئ)

### الأعراض:
- `/Health` يظهر عدد كبير في Dead Letter Queue
- Dashboard يظهر نسبة فشل عالية

### الإجراء:
1. **اذهب إلى `/Health`** وراجع أسباب الفشل
2. **الأسباب الشائعة:**

   | السبب | الحل |
   |:---|:---|
   | `OU does not exist` | أنشئ الـ OU في AD أولاً |
   | `Duplicate sAMAccountName` | تحقق من الطالب في Oracle — قد يكون مكرر |
   | `Invalid characters` | تحقق من بيانات الطالب في Oracle |
   | `Access denied` | تحقق من صلاحيات حساب AD المستخدم |

3. **بعد إصلاح السبب** → اضغط 🔄 لإعادة تشغيل العملية
4. **لإعادة تشغيل الكل:**
   ```
   POST /Health/ReplayAllDeadLetters
   ```

---

## 🚨 السيناريو 5: طالب جديد لم يُنشأ حسابه

### الإجراء:
1. **تحقق من وجوده في Oracle:**
   ```sql
   SELECT * FROM V_STUDENT_DATA WHERE STUDENT_ID = 'XXXXX'
   ```
2. **تحقق من Metaverse:**
   ```sql
   SELECT * FROM MetaverseEntries WHERE ExternalId = 'XXXXX'
   ```
3. **تحقق من DLQ:**
   ```sql
   SELECT * FROM DeadLetterEntries WHERE StudentId = 'XXXXX'
   ```
4. **شغّل مزامنة فردية:**
   - من Dashboard → أدخل رقم الطالب → "مزامنة فردية"
5. **تتبع بالـ CorrelationId:**
   ```sql
   SELECT CorrelationId FROM SyncRuns ORDER BY StartTime DESC
   -- ثم:
   SELECT * FROM AuditEntries WHERE CorrelationId = 'XXX'
   ```

---

## 🚨 السيناريو 6: التطبيق لا يعمل بعد التحديث

### الإجراء:
1. **تحقق من الـ Logs:** `Logs/identitysync-*.log`
2. **مشاكل شائعة:**

   | الخطأ | الحل |
   |:---|:---|
   | `Invalid column name` | Migration لم يُطبّق — شغّل التطبيق مرة ليطبقه تلقائياً |
   | `Could not load assembly` | NuGet packages ناقصة — `dotnet restore` |
   | `Connection string` | تحقق من `appsettings.json` |

3. **للرجوع لإصدار سابق:**
   ```bash
   git log --oneline -5
   git checkout <commit-hash>
   dotnet run --project src\IdentitySyncPro.Web
   ```

---

## 📊 المراقبة اليومية

### فحص سريع (دقيقتين):
1. ✅ افتح `/Health` — تحقق أن كل المكونات `Healthy`
2. ✅ افتح `/hangfire` — تحقق أن لا توجد Jobs فاشلة
3. ✅ افتح Dashboard — تحقق من آخر مزامنة ناجحة

### فحص أسبوعي (10 دقائق):
1. ✅ راجع Dead Letter Queue — هل هناك عمليات معلقة؟
2. ✅ راجع Quarantine — هل هناك هويات محجورة؟
3. ✅ تحقق من حجم قاعدة البيانات
4. ✅ تحقق من حجم ملفات الـ Logs

### فحص شهري (30 دقيقة):
1. ✅ راجع Audit Entries — هل هناك أنماط فشل متكررة؟
2. ✅ راجع القواعد — هل تحتاج تحديث؟
3. ✅ تحقق من صلاحيات حساب AD (انتهاء كلمة المرور)
4. ✅ خذ نسخة احتياطية من القاعدة

---

## 🔑 أوامر مفيدة

### PowerShell:
```powershell
# تشغيل التطبيق
dotnet run --project src\IdentitySyncPro.Web

# بناء المشروع
dotnet build IdentitySyncPro.sln

# تشغيل الاختبارات
dotnet test src\IdentitySyncPro.Tests

# البحث في الـ Logs
Select-String -Path "Logs\identitysync-*.log" -Pattern "ERROR"
Select-String -Path "Logs\identitysync-*.log" -Pattern "CorrelationId"

# فحص اتصال AD
Test-NetConnection DC01 -Port 389
```

### SQL:
```sql
-- آخر 10 عمليات مزامنة
SELECT TOP 10 * FROM SyncRuns ORDER BY StartTime DESC

-- العمليات الفاشلة اليوم
SELECT * FROM SyncOperations 
WHERE Status = 3 AND Timestamp > CAST(GETDATE() AS DATE)

-- البحث بـ CorrelationId
SELECT * FROM AuditEntries WHERE CorrelationId = 'XXX'

-- إحصائيات DLQ
SELECT IsReplayed, COUNT(*) as Total FROM DeadLetterEntries GROUP BY IsReplayed

-- حجم الجداول
SELECT t.name, SUM(p.rows) as RowCount
FROM sys.tables t JOIN sys.partitions p ON t.object_id = p.object_id
WHERE p.index_id < 2 GROUP BY t.name ORDER BY RowCount DESC
```

---

## 🔒 قواعد السلامة الذهبية

> **⛔ هذه القواعد لا يمكن كسرها أبداً:**

1. **لا تحذف حسابات AD يدوياً** — استخدم OU Relocation فقط
2. **لا تعطل حسابات AD عبر النظام** — Safe Sync يمنع هذا
3. **لا تحذف من `MetaverseEntries`** — هي المرجع الوحيد
4. **لا تعدل `SyncEngine.cs`** بدون تشغيل `SafeSyncTests`
5. **خذ نسخة احتياطية** قبل أي تحديث للنظام

---

## 📞 التصعيد

| المستوى | من يتعامل | متى |
|:---|:---|:---|
| **L1** | مشغّل النظام | Dashboard أحمر، Jobs فاشلة |
| **L2** | مسؤول AD/Oracle | اتصال فاشل، صلاحيات |
| **L3** | المطور | خطأ في الكود، تحديث النظام |

---

*آخر تحديث: مايو 2026 · الإصدار: 1.0*
