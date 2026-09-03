const puppeteer = require('puppeteer');
const fs = require('fs');
const path = require('path');

const html = `<!DOCTYPE html>
<html lang="ar" dir="rtl">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>IdentitySyncPro - تقرير المميزات</title>
<style>
  @import url('https://fonts.googleapis.com/css2?family=Tajawal:wght@300;400;500;700;800;900&display=swap');

  * { margin: 0; padding: 0; box-sizing: border-box; }

  body {
    font-family: 'Tajawal', 'Arial', sans-serif;
    direction: rtl;
    background: #f8fafc;
    color: #1e293b;
    font-size: 13px;
    line-height: 1.7;
  }

  /* === COVER PAGE === */
  .cover {
    page-break-after: always;
    background: linear-gradient(135deg, #0f172a 0%, #1e3a5f 50%, #0f4c81 100%);
    min-height: 100vh;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    text-align: center;
    color: white;
    padding: 60px 40px;
    position: relative;
    overflow: hidden;
  }
  .cover::before {
    content: '';
    position: absolute;
    top: -50%;
    left: -50%;
    width: 200%;
    height: 200%;
    background: radial-gradient(ellipse at center, rgba(59,130,246,0.15) 0%, transparent 70%);
  }
  .cover-badge {
    background: rgba(59,130,246,0.2);
    border: 1px solid rgba(59,130,246,0.5);
    color: #93c5fd;
    padding: 6px 20px;
    border-radius: 50px;
    font-size: 12px;
    letter-spacing: 2px;
    margin-bottom: 30px;
    text-transform: uppercase;
  }
  .cover-logo {
    font-size: 52px;
    font-weight: 900;
    background: linear-gradient(135deg, #60a5fa, #a78bfa, #34d399);
    -webkit-background-clip: text;
    -webkit-text-fill-color: transparent;
    margin-bottom: 10px;
    position: relative;
  }
  .cover-subtitle {
    font-size: 20px;
    color: #94a3b8;
    margin-bottom: 40px;
    font-weight: 300;
  }
  .cover-tagline {
    font-size: 28px;
    font-weight: 700;
    color: white;
    max-width: 600px;
    line-height: 1.4;
    margin-bottom: 50px;
  }
  .cover-stats {
    display: flex;
    gap: 40px;
    margin-top: 10px;
  }
  .cover-stat {
    text-align: center;
  }
  .cover-stat-num {
    font-size: 36px;
    font-weight: 900;
    color: #60a5fa;
  }
  .cover-stat-label {
    font-size: 11px;
    color: #64748b;
    margin-top: 4px;
  }
  .cover-divider {
    width: 80px;
    height: 3px;
    background: linear-gradient(90deg, #3b82f6, #8b5cf6);
    margin: 40px auto;
    border-radius: 2px;
  }
  .cover-footer {
    position: absolute;
    bottom: 30px;
    font-size: 11px;
    color: #475569;
  }

  /* === CONTENT === */
  .content { padding: 0; }

  .page-section {
    padding: 40px 50px;
    page-break-inside: avoid;
  }

  /* === HEADER BAR === */
  .section-header {
    background: linear-gradient(135deg, #1e3a5f, #0f4c81);
    color: white;
    padding: 14px 50px;
    margin-bottom: 0;
    display: flex;
    align-items: center;
    gap: 12px;
    page-break-after: avoid;
  }
  .section-num {
    background: rgba(96,165,250,0.3);
    border: 1px solid rgba(96,165,250,0.5);
    color: #93c5fd;
    width: 32px;
    height: 32px;
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    font-weight: 700;
    font-size: 14px;
    flex-shrink: 0;
  }
  .section-title {
    font-size: 18px;
    font-weight: 700;
  }

  /* === CARDS === */
  .card {
    background: white;
    border-radius: 12px;
    padding: 24px;
    margin-bottom: 20px;
    border: 1px solid #e2e8f0;
    box-shadow: 0 2px 8px rgba(0,0,0,0.05);
  }
  .card-title {
    font-size: 15px;
    font-weight: 700;
    color: #1e40af;
    margin-bottom: 12px;
    padding-bottom: 8px;
    border-bottom: 2px solid #dbeafe;
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .card-title .icon {
    font-size: 18px;
  }

  /* === GRID === */
  .grid-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
  .grid-3 { display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 16px; }

  /* === TABLE === */
  table {
    width: 100%;
    border-collapse: collapse;
    margin: 12px 0;
    font-size: 12px;
  }
  th {
    background: linear-gradient(135deg, #1e3a5f, #1e40af);
    color: white;
    padding: 10px 14px;
    text-align: right;
    font-weight: 600;
    font-size: 12px;
  }
  td {
    padding: 9px 14px;
    border-bottom: 1px solid #f1f5f9;
    color: #334155;
  }
  tr:nth-child(even) td { background: #f8fafc; }
  tr:hover td { background: #eff6ff; }

  /* === FEATURE BADGES === */
  .badge {
    display: inline-block;
    padding: 3px 10px;
    border-radius: 50px;
    font-size: 11px;
    font-weight: 600;
    margin: 2px;
  }
  .badge-blue { background: #dbeafe; color: #1d4ed8; }
  .badge-green { background: #dcfce7; color: #15803d; }
  .badge-purple { background: #ede9fe; color: #7c3aed; }
  .badge-orange { background: #ffedd5; color: #c2410c; }
  .badge-red { background: #fee2e2; color: #dc2626; }

  /* === SAFE SYNC BOX === */
  .safe-sync-box {
    background: linear-gradient(135deg, #0f172a, #1e293b);
    border: 1px solid #334155;
    border-radius: 12px;
    padding: 24px;
    color: white;
    margin: 16px 0;
  }
  .safe-sync-box h3 {
    color: #f87171;
    font-size: 16px;
    margin-bottom: 16px;
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .safe-sync-row {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 8px 0;
    border-bottom: 1px solid #1e293b;
    font-size: 13px;
  }
  .safe-sync-row:last-child { border-bottom: none; }
  .check-green { color: #4ade80; font-size: 16px; }
  .check-red { color: #f87171; font-size: 16px; }

  /* === STAT BOXES === */
  .stat-box {
    background: linear-gradient(135deg, #1e40af, #1d4ed8);
    border-radius: 12px;
    padding: 20px;
    color: white;
    text-align: center;
  }
  .stat-box .num {
    font-size: 36px;
    font-weight: 900;
    color: #93c5fd;
    line-height: 1;
  }
  .stat-box .label {
    font-size: 12px;
    color: #bfdbfe;
    margin-top: 6px;
  }

  /* === COMPARISON TABLE === */
  .comparison-table th:first-child { text-align: right; }
  .comparison-table td:not(:first-child) { text-align: center; }
  .yes { color: #16a34a; font-weight: 700; font-size: 16px; }
  .no { color: #dc2626; font-weight: 700; font-size: 16px; }
  .partial { color: #d97706; font-weight: 700; }
  .highlight-col td { background: #eff6ff !important; border-right: 3px solid #3b82f6; }

  /* === TIMELINE === */
  .timeline {
    display: flex;
    flex-direction: column;
    gap: 0;
    position: relative;
    padding-right: 24px;
  }
  .timeline::before {
    content: '';
    position: absolute;
    right: 7px;
    top: 0;
    bottom: 0;
    width: 2px;
    background: linear-gradient(to bottom, #3b82f6, #8b5cf6);
  }
  .timeline-item {
    display: flex;
    gap: 16px;
    padding-bottom: 20px;
    position: relative;
  }
  .timeline-dot {
    width: 16px;
    height: 16px;
    border-radius: 50%;
    background: #3b82f6;
    border: 3px solid white;
    box-shadow: 0 0 0 2px #3b82f6;
    flex-shrink: 0;
    margin-right: -32px;
    margin-top: 2px;
  }
  .timeline-content {
    background: white;
    border: 1px solid #e2e8f0;
    border-radius: 8px;
    padding: 12px 16px;
    flex: 1;
  }
  .timeline-title {
    font-weight: 700;
    color: #1e40af;
    font-size: 13px;
  }
  .timeline-desc {
    color: #64748b;
    font-size: 12px;
    margin-top: 4px;
  }

  /* === ROI TABLE === */
  .roi-table td:first-child { font-weight: 600; color: #1e293b; }
  .roi-table td:nth-child(2) { color: #dc2626; }
  .roi-table td:nth-child(3) { color: #16a34a; font-weight: 700; }

  /* === PAGE BREAK === */
  .page-break { page-break-after: always; }

  /* === HIGHLIGHT BOX === */
  .highlight-box {
    background: linear-gradient(135deg, #eff6ff, #f0fdf4);
    border: 1px solid #bfdbfe;
    border-radius: 10px;
    padding: 16px 20px;
    margin: 12px 0;
    font-size: 13px;
    color: #1e40af;
    font-weight: 500;
  }

  /* === FOOTER === */
  .page-footer {
    background: #0f172a;
    color: #64748b;
    padding: 16px 50px;
    text-align: center;
    font-size: 11px;
    page-break-inside: avoid;
  }

  /* === PRINT === */
  @media print {
    body { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
  }
</style>
</head>
<body>

<!-- ===== COVER PAGE ===== -->
<div class="cover">
  <div class="cover-badge">Enterprise IAM Solution • 2026</div>
  <div class="cover-logo">IdentitySyncPro</div>
  <div class="cover-subtitle">منصة إدارة الهويات المؤسسية</div>
  <div class="cover-tagline">
    حساب AD جاهز للطالب فور قبوله —<br>تلقائياً، بأمان، وبدون أخطاء
  </div>
  <div class="cover-divider"></div>
  <div class="cover-stats">
    <div class="cover-stat">
      <div class="cover-stat-num">34</div>
      <div class="cover-stat-label">ربط حقل افتراضي</div>
    </div>
    <div class="cover-stat">
      <div class="cover-stat-num">28</div>
      <div class="cover-stat-label">اختبار ناجح 100%</div>
    </div>
    <div class="cover-stat">
      <div class="cover-stat-num">0</div>
      <div class="cover-stat-label">حذف أو تعطيل حساب</div>
    </div>
    <div class="cover-stat">
      <div class="cover-stat-num">∞</div>
      <div class="cover-stat-label">مرونة الإعداد</div>
    </div>
  </div>
  <div class="cover-footer">تقرير المميزات التسويقي | الإصدار 2.0 | مايو 2026</div>
</div>

<!-- ===== SECTION 1: المشكلة ===== -->
<div class="section-header">
  <div class="section-num">1</div>
  <div class="section-title">المشكلة التي تحلّها المنصة</div>
</div>
<div class="page-section">
  <div class="grid-2">
    <div class="card">
      <div class="card-title"><span class="icon">⚠️</span> التحديات الحالية</div>
      <table>
        <tr><th>التحدي</th><th>التأثير</th></tr>
        <tr><td>إدارة آلاف الحسابات يدوياً</td><td>ساعات مهدرة + أخطاء متكررة</td></tr>
        <tr><td>تأخر تفعيل حسابات الطلاب</td><td>تجربة أكاديمية سلبية</td></tr>
        <tr><td>حسابات مهجورة لخريجين</td><td>ثغرات أمنية + هدر تراخيص</td></tr>
        <tr><td>صعوبة التتبع والمراجعة</td><td>عدم الامتثال الأمني</td></tr>
        <tr><td>الاعتماد على PowerShell هشّ</td><td>توقف العمليات عند أي تغيير</td></tr>
      </table>
    </div>
    <div class="card">
      <div class="card-title"><span class="icon">✅</span> الحل مع IdentitySyncPro</div>
      <div class="timeline">
        <div class="timeline-item">
          <div class="timeline-dot"></div>
          <div class="timeline-content">
            <div class="timeline-title">أتمتة 100% للحسابات</div>
            <div class="timeline-desc">لا تدخل يدوي في الحالات الاعتيادية</div>
          </div>
        </div>
        <div class="timeline-item">
          <div class="timeline-dot"></div>
          <div class="timeline-content">
            <div class="timeline-title">تفعيل خلال 30 دقيقة</div>
            <div class="timeline-desc">من لحظة القبول حتى الحساب جاهز</div>
          </div>
        </div>
        <div class="timeline-item">
          <div class="timeline-dot"></div>
          <div class="timeline-content">
            <div class="timeline-title">Safe Sync — صفر مخاطر</div>
            <div class="timeline-desc">لا حذف، لا تعطيل، لا مفاجآت</div>
          </div>
        </div>
        <div class="timeline-item">
          <div class="timeline-dot"></div>
          <div class="timeline-content">
            <div class="timeline-title">Audit Trail كامل</div>
            <div class="timeline-desc">كل عملية موثقة ومسجلة للمراجعة</div>
          </div>
        </div>
      </div>
    </div>
  </div>
</div>

<!-- ===== SECTION 2: المزامنة ===== -->
<div class="section-header">
  <div class="section-num">2</div>
  <div class="section-title">المزامنة الذكية الآلية</div>
</div>
<div class="page-section">
  <div class="grid-2">
    <div class="card">
      <div class="card-title"><span class="icon">🔄</span> مزامنة كاملة (Full Sync)</div>
      <p style="color:#64748b;font-size:12px;margin-bottom:10px">مزامنة شاملة لجميع الطلاب</p>
      <div style="padding:8px;background:#f8fafc;border-radius:8px;font-size:12px">
        <div>✅ قراءة كل سجلات Oracle دفعة واحدة</div>
        <div>✅ مقارنة تلقائية مع AD بالكامل</div>
        <div>✅ إنشاء أو تحديث بناءً على الفروقات</div>
        <div style="margin-top:8px;color:#3b82f6">⏰ الاستخدام: أول تشغيل أو إعادة معايرة</div>
      </div>
    </div>
    <div class="card">
      <div class="card-title"><span class="icon">⚡</span> مزامنة تغييرات (Delta Sync)</div>
      <p style="color:#64748b;font-size:12px;margin-bottom:10px">معالجة المتغيرين فقط</p>
      <div style="padding:8px;background:#f8fafc;border-radius:8px;font-size:12px">
        <div>✅ أسرع بكثير من Full Sync</div>
        <div>✅ يعالج فقط ما تغيّر</div>
        <div>✅ جدولة تلقائية كل 30 دقيقة</div>
        <div style="margin-top:8px;color:#3b82f6">⏰ الاستخدام: التشغيل اليومي المستمر</div>
      </div>
    </div>
    <div class="card">
      <div class="card-title"><span class="icon">👤</span> مزامنة فردية (Single Sync)</div>
      <p style="color:#64748b;font-size:12px;margin-bottom:10px">تفعيل طالب واحد فوراً</p>
      <div style="padding:8px;background:#f8fafc;border-radius:8px;font-size:12px">
        <div>✅ أدخل رقم الطالب → نتيجة فورية</div>
        <div>✅ يعرض الحقول المتغيرة بالتفصيل</div>
        <div>✅ يدعم Enter للتنفيذ السريع</div>
        <div style="margin-top:8px;color:#3b82f6">⏰ الاستخدام: الطوارئ والدعم الفوري</div>
      </div>
    </div>
    <div class="card">
      <div class="card-title"><span class="icon">👁️</span> التشغيل التجريبي (Dry Run)</div>
      <p style="color:#64748b;font-size:12px;margin-bottom:10px">محاكاة بدون أي تأثير</p>
      <div style="padding:8px;background:#f0fdf4;border-radius:8px;font-size:12px;border:1px solid #bbf7d0">
        <div>✅ محاكاة كاملة للمزامنة</div>
        <div>✅ يعرض: ماذا سيُنشأ وماذا سيُحدَّث</div>
        <div>✅ <strong>صفر تأثير</strong> على Active Directory</div>
        <div style="margin-top:8px;color:#16a34a">⏰ الاستخدام: قبل أي تغيير في الإنتاج</div>
      </div>
    </div>
  </div>
</div>

<!-- ===== SAFE SYNC ===== -->
<div class="section-header">
  <div class="section-num">3</div>
  <div class="section-title">وضع المزامنة الآمنة (Safe Sync) ⛔ لا تفاوض</div>
</div>
<div class="page-section">
  <div class="safe-sync-box">
    <h3>⛔ الضمان الأمني المطلق — مُدمج في قلب النظام</h3>
    <div class="safe-sync-row">
      <span class="check-green">✓</span>
      <span>إنشاء حسابات AD جديدة للطلاب المستجدين</span>
    </div>
    <div class="safe-sync-row">
      <span class="check-green">✓</span>
      <span>تحديث بيانات الحسابات (الاسم، الكلية، البريد...)</span>
    </div>
    <div class="safe-sync-row">
      <span class="check-green">✓</span>
      <span>نقل الحسابات بين الـ OUs</span>
    </div>
    <div class="safe-sync-row">
      <span class="check-green">✓</span>
      <span>إضافة إلى مجموعات الأمان و Microsoft 365</span>
    </div>
    <div class="safe-sync-row">
      <span class="check-red">✗</span>
      <span style="color:#fca5a5"><strong>حذف حساب AD — ممنوع نهائياً ولا يمكن تجاوزه</strong></span>
    </div>
    <div class="safe-sync-row">
      <span class="check-red">✗</span>
      <span style="color:#fca5a5"><strong>تعطيل حساب AD — ممنوع نهائياً ولا يمكن تجاوزه</strong></span>
    </div>
  </div>
  <div class="highlight-box">
    💡 هذا الضمان ليس إعداداً قابلاً للتغيير — بل مُدمج في قلب محرك المزامنة ولا يمكن تجاوزه حتى بالخطأ
  </div>
</div>

<!-- ===== SECTION 4: MAPPING ===== -->
<div class="section-header">
  <div class="section-num">4</div>
  <div class="section-title">ربط الحقول الديناميكي — مرونة بلا حدود</div>
</div>
<div class="page-section">
  <div class="grid-2">
    <div class="card">
      <div class="card-title"><span class="icon">🔗</span> 34 ربط افتراضي جاهز</div>
      <table>
        <tr><th>المجموعة</th><th>الحقول</th></tr>
        <tr><td>Core Identity</td><td>sAMAccountName, employeeID, givenName, sn, initials, displayName</td></tr>
        <tr><td>Email & Proxy</td><td>mail, UPN, mailNickname, proxyAddresses×2, targetAddress</td></tr>
        <tr><td>Contact</td><td>mobile, telephoneNumber, department, title</td></tr>
        <tr><td>Extension Attribs</td><td>extensionAttribute 1-6, 11, 13-15</td></tr>
        <tr><td>Gender/Location</td><td>physicalDeliveryOfficeName, info, co, l</td></tr>
        <tr><td>HR Standard</td><td>employeeNumber, employeeType, company</td></tr>
      </table>
    </div>
    <div class="card">
      <div class="card-title"><span class="icon">⚙️</span> التحويلات الذكية</div>
      <table>
        <tr><th>التحويل</th><th>الوصف</th><th>مثال</th></tr>
        <tr><td><span class="badge badge-blue">Format</span></td><td>تنسيق النص</td><td>{0}@example.com</td></tr>
        <tr><td><span class="badge badge-purple">Concat</span></td><td>دمج حقول</td><td>{الاسم} {الأب} {اللقب}</td></tr>
        <tr><td><span class="badge badge-green">Map</span></td><td>تحويل القيم</td><td>1=MALE, 2=FEMALE</td></tr>
        <tr><td><span class="badge badge-orange">GetInitials</span></td><td>ذكي: >4 → أول حرف</td><td>عبدالرحمن → ع</td></tr>
        <tr><td><span class="badge badge-blue">Static</span></td><td>قيمة ثابتة</td><td>"Student" دائماً</td></tr>
        <tr><td><span class="badge badge-purple">ToUpper/Lower</span></td><td>تحويل الحالة</td><td>ahmed → AHMED</td></tr>
      </table>
      <div style="margin-top:12px;padding:10px;background:#eff6ff;border-radius:8px;font-size:12px">
        <strong>🌟 Multi-Value Support:</strong><br>
        proxyAddresses يدعم قيم متعددة — أضف صفين لنفس الـ Attribute والمحرك يجمعهما تلقائياً
      </div>
    </div>
  </div>
</div>

<!-- ===== SECTION 5 & 6 ===== -->
<div class="section-header">
  <div class="section-num">5</div>
  <div class="section-title">محرك القواعد ودورة الحياة</div>
</div>
<div class="page-section">
  <div class="grid-2">
    <div class="card">
      <div class="card-title"><span class="icon">⚙️</span> محرك القواعد (Rules Engine)</div>
      <table>
        <tr><th>النوع</th><th>الوظيفة</th></tr>
        <tr><td><span class="badge badge-blue">Join</span></td><td>ربط هوية المصدر بحساب AD</td></tr>
        <tr><td><span class="badge badge-purple">Projection</span></td><td>إنشاء هوية جديدة في Metaverse</td></tr>
        <tr><td><span class="badge badge-green">ImportFlow</span></td><td>Oracle → Metaverse</td></tr>
        <tr><td><span class="badge badge-orange">ExportFlow</span></td><td>Metaverse → Active Directory</td></tr>
        <tr><td><span class="badge badge-blue">Provisioning</span></td><td>إنشاء حساب AD للطالب</td></tr>
        <tr><td><span class="badge badge-red">Deprovisioning</span></td><td>محمي بـ Safe Sync ⛔</td></tr>
      </table>
      <div style="margin-top:12px;padding:10px;background:#f0fdf4;border-radius:8px;font-size:12px">
        <strong>Rule Versioning:</strong> كل تعديل يُنشئ إصدار جديد — Rollback لأي إصدار سابق
      </div>
    </div>
    <div class="card">
      <div class="card-title"><span class="icon">🔄</span> دورة الحياة (Lifecycle)</div>
      <table>
        <tr><th>الإجراء</th><th>الوصف</th></tr>
        <tr><td><span class="badge badge-green">SetState</span></td><td>تغيير حالة: Active/Suspended/Graduated</td></tr>
        <tr><td><span class="badge badge-blue">MoveOU</span></td><td>نقل الحساب بين الـ OUs</td></tr>
        <tr><td><span class="badge badge-green">EnableAD</span></td><td>تفعيل الحساب</td></tr>
        <tr><td><span class="badge badge-purple">AddGroups</span></td><td>إضافة لمجموعات الأمان</td></tr>
        <tr><td><span class="badge badge-orange">SendSMS</span></td><td>إشعار SMS فوري للطالب</td></tr>
        <tr><td><span class="badge badge-blue">Reactivate</span></td><td>إعادة تفعيل كاملة</td></tr>
      </table>
      <div style="margin-top:12px;padding:10px;background:#eff6ff;border-radius:8px;font-size:12px">
        <strong>Grace Period:</strong> "انتظر 30 يوم قبل تغيير الحالة" — لو أُعيد القبول خلالها لا شيء يتغير
      </div>
    </div>
  </div>
</div>

<!-- ===== SECTION 6: MONITORING ===== -->
<div class="section-header">
  <div class="section-num">6</div>
  <div class="section-title">المراقبة والأمان المؤسسي</div>
</div>
<div class="page-section">
  <div class="grid-3">
    <div class="card" style="text-align:center">
      <div style="font-size:32px;margin-bottom:8px">🔌</div>
      <div class="card-title" style="justify-content:center">Circuit Breaker</div>
      <div style="font-size:12px;color:#64748b">
        3 فشل متتالية → قطع الاتصال تلقائياً<br>
        حماية من إغراق الأنظمة<br>
        استئناف تلقائي بعد 5 دقائق
      </div>
    </div>
    <div class="card" style="text-align:center">
      <div style="font-size:32px;margin-bottom:8px">🏥</div>
      <div class="card-title" style="justify-content:center">Quarantine</div>
      <div style="font-size:12px;color:#64748b">
        هوية تفشل 3+ مرات → عزل تلقائي<br>
        لا تؤثر على باقي العمليات<br>
        حل يدوي مع توثيق السبب
      </div>
    </div>
    <div class="card" style="text-align:center">
      <div style="font-size:32px;margin-bottom:8px">📬</div>
      <div class="card-title" style="justify-content:center">Dead Letter Queue</div>
      <div style="font-size:12px;color:#64748b">
        عمليات فشلت نهائياً → تُحفظ<br>
        إعادة تشغيل بزر واحد<br>
        تفاصيل كاملة لكل خطأ
      </div>
    </div>
    <div class="card" style="text-align:center">
      <div style="font-size:32px;margin-bottom:8px">📡</div>
      <div class="card-title" style="justify-content:center">Live Monitor</div>
      <div style="font-size:12px;color:#64748b">
        بث مباشر عبر SignalR<br>
        عداد فوري لكل عملية<br>
        شريط تقدم مرئي
      </div>
    </div>
    <div class="card" style="text-align:center">
      <div style="font-size:32px;margin-bottom:8px">📋</div>
      <div class="card-title" style="justify-content:center">Audit Trail</div>
      <div style="font-size:12px;color:#64748b">
        كل عملية موثقة بالكامل<br>
        CorrelationId للتتبع الدقيق<br>
        بحث بالاسم أو الرقم
      </div>
    </div>
    <div class="card" style="text-align:center">
      <div style="font-size:32px;margin-bottom:8px">💬</div>
      <div class="card-title" style="justify-content:center">SMS Notifications</div>
      <div style="font-size:12px;color:#64748b">
        بيانات الدخول للطلاب الجدد<br>
        كلمات مرور عشوائية آمنة<br>
        تكامل مع أي SMS API
      </div>
    </div>
  </div>
</div>

<!-- ===== COMPARISON ===== -->
<div class="section-header">
  <div class="section-num">7</div>
  <div class="section-title">المقارنة مع البدائل</div>
</div>
<div class="page-section">
  <table class="comparison-table">
    <tr>
      <th>الميزة</th>
      <th>IdentitySyncPro</th>
      <th>PowerShell يدوي</th>
      <th>Azure AD Connect</th>
    </tr>
    <tr class="highlight-col">
      <td>واجهة عربية RTL</td>
      <td class="yes">✓</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
    </tr>
    <tr class="highlight-col">
      <td>Safe Sync مُدمج</td>
      <td class="yes">✓</td>
      <td class="no">✗</td>
      <td class="partial">⚠ جزئي</td>
    </tr>
    <tr class="highlight-col">
      <td>Multi-Tenant</td>
      <td class="yes">✓</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
    </tr>
    <tr class="highlight-col">
      <td>Dry Run تجريبي</td>
      <td class="yes">✓</td>
      <td class="no">✗</td>
      <td class="partial">⚠</td>
    </tr>
    <tr class="highlight-col">
      <td>Grace Period</td>
      <td class="yes">✓</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
    </tr>
    <tr class="highlight-col">
      <td>إشعارات SMS</td>
      <td class="yes">✓</td>
      <td class="partial">يدوي</td>
      <td class="no">✗</td>
    </tr>
    <tr class="highlight-col">
      <td>Audit Trail</td>
      <td class="yes">✓</td>
      <td class="partial">محدود</td>
      <td class="yes">✓</td>
    </tr>
    <tr class="highlight-col">
      <td>Circuit Breaker</td>
      <td class="yes">✓</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
    </tr>
    <tr class="highlight-col">
      <td>Rule Versioning</td>
      <td class="yes">✓</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
    </tr>
    <tr class="highlight-col">
      <td>دعم Oracle</td>
      <td class="yes">✓</td>
      <td class="yes">✓</td>
      <td class="no">✗</td>
    </tr>
    <tr class="highlight-col">
      <td>Live Monitor</td>
      <td class="yes">✓</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
    </tr>
    <tr class="highlight-col">
      <td>التكلفة</td>
      <td style="color:#16a34a;text-align:center;font-weight:700">🟢 مناسبة</td>
      <td style="color:#16a34a;text-align:center;font-weight:700">🟢 مجانية</td>
      <td style="color:#dc2626;text-align:center;font-weight:700">🔴 مرتفعة</td>
    </tr>
  </table>
</div>

<!-- ===== ROI ===== -->
<div class="section-header">
  <div class="section-num">8</div>
  <div class="section-title">العائد على الاستثمار (ROI)</div>
</div>
<div class="page-section">
  <div class="grid-3" style="margin-bottom:20px">
    <div class="stat-box">
      <div class="num">95%</div>
      <div class="label">تخفيض في المهام اليدوية</div>
    </div>
    <div class="stat-box">
      <div class="num">&lt;30د</div>
      <div class="label">من القبول حتى الحساب جاهز</div>
    </div>
    <div class="stat-box">
      <div class="num">0</div>
      <div class="label">أخطاء ربط البيانات</div>
    </div>
  </div>
  <table class="roi-table">
    <tr>
      <th>البند</th>
      <th>قبل IdentitySyncPro</th>
      <th>بعد IdentitySyncPro</th>
    </tr>
    <tr>
      <td>وقت تفعيل حساب جديد</td>
      <td>1-3 أيام (يدوي)</td>
      <td>أقل من 30 دقيقة (آلي)</td>
    </tr>
    <tr>
      <td>أخطاء البيانات</td>
      <td>متكررة بسبب الإدخال اليدوي</td>
      <td>صفر أخطاء ربط</td>
    </tr>
    <tr>
      <td>حسابات مهجورة</td>
      <td>تتراكم بدون رقابة</td>
      <td>صفر (Grace Period + إدارة ذكية)</td>
    </tr>
    <tr>
      <td>وقت المسؤول يومياً</td>
      <td>2-4 ساعات إدارة يدوية</td>
      <td>أقل من 15 دقيقة مراجعة</td>
    </tr>
    <tr>
      <td>امتثال الأمان</td>
      <td>جزئي وصعب التحقق</td>
      <td>100% موثق في Audit Trail</td>
    </tr>
    <tr>
      <td>تراخيص Microsoft 365</td>
      <td>هدر على حسابات غير مستخدمة</td>
      <td>ترشيد دقيق بناءً على الحالة</td>
    </tr>
  </table>
</div>

<!-- ===== TECH SPECS ===== -->
<div class="section-header">
  <div class="section-num">9</div>
  <div class="section-title">المواصفات التقنية</div>
</div>
<div class="page-section">
  <div class="grid-2">
    <div class="card">
      <div class="card-title"><span class="icon">🛠️</span> Stack التقني</div>
      <table>
        <tr><th>المكوّن</th><th>التقنية</th></tr>
        <tr><td>المنصة</td><td>ASP.NET Core 8.0 MVC</td></tr>
        <tr><td>ORM</td><td>Entity Framework Core + Migrations</td></tr>
        <tr><td>بروتوكول AD</td><td>LDAP / LDAPS</td></tr>
        <tr><td>الجدولة</td><td>Hangfire</td></tr>
        <tr><td>البث المباشر</td><td>SignalR</td></tr>
        <tr><td>الاختبارات</td><td>28/28 اختبار وحدة ✅</td></tr>
        <tr><td>اللغة</td><td>عربي + إنجليزي (RTL/LTR)</td></tr>
      </table>
    </div>
    <div class="card">
      <div class="card-title"><span class="icon">🗄️</span> قواعد البيانات المدعومة</div>
      <div style="display:flex;flex-wrap:wrap;gap:8px;margin-bottom:16px">
        <span class="badge badge-blue">Oracle</span>
        <span class="badge badge-purple">SQL Server</span>
        <span class="badge badge-green">PostgreSQL</span>
        <span class="badge badge-orange">MySQL</span>
      </div>
      <div class="card-title" style="margin-top:16px"><span class="icon">⏰</span> جدولة Hangfire</div>
      <table>
        <tr><th>المهمة</th><th>الجدول</th></tr>
        <tr><td>Full Sync</td><td>يومياً 2:00 AM</td></tr>
        <tr><td>Delta Sync</td><td>كل 30 دقيقة</td></tr>
        <tr><td>Health Check</td><td>كل 10 دقائق</td></tr>
        <tr><td>Data Retention</td><td>أسبوعياً</td></tr>
      </table>
    </div>
  </div>
</div>

<!-- ===== FOOTER ===== -->
<div class="page-footer">
  <strong style="color:#94a3b8">IdentitySyncPro</strong> — منصة إدارة الهويات المؤسسية |
  الإصدار 2.0 | مايو 2026 |
  هذا التقرير سري ومخصص للاستخدام التسويقي الداخلي
</div>

</body>
</html>`;

async function generatePDF() {
  const browser = await puppeteer.launch({
    headless: true,
    executablePath: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    args: ['--no-sandbox', '--disable-setuid-sandbox', '--lang=ar', '--disable-web-security']
  });

  const page = await browser.newPage();
  await page.setContent(html, { waitUntil: 'networkidle0', timeout: 30000 });

  await page.pdf({
    path: 'c:/ReactProjects/IdentitySyncPro/docs/IdentitySyncPro_Marketing_Report.pdf',
    format: 'A4',
    printBackground: true,
    margin: { top: '0mm', right: '0mm', bottom: '0mm', left: '0mm' }
  });

  await browser.close();
  console.log('PDF generated successfully!');
}

generatePDF().catch(console.error);
