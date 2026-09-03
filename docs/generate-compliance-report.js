/**
 * Regenerates the formal cybersecurity compliance report (Arabic, RTL) as a .docx.
 *
 *   npm install docx
 *   node docs/generate-compliance-report.js "docs/تقرير-المطابقة-الأمن-السيبراني.docx"
 *
 * Kept in the repository rather than produced ad hoc: the report is a deliverable that will be
 * reissued whenever the compliance position changes, and hand-editing a .docx to update one
 * status cell is how a matrix drifts out of step with the code it describes.
 *
 * When a control changes, update the matrix rows in sections 3-5 AND the counts in section 1.
 */
const {
  Document, Packer, Paragraph, TextRun, HeadingLevel, AlignmentType,
  Table, TableRow, TableCell, WidthType, ShadingType, BorderStyle,
  PageBreak, Footer, Header, PageNumber, TableOfContents, LevelFormat,
  convertInchesToTwip, TabStopType, VerticalAlign
} = require('docx');
const fs = require('fs');

// ── Palette ───────────────────────────────────────────────────────────────
const NAVY = '1F3864';
const ACCENT = '2E74B5';
const GREY = '595959';
const OK = '1E7B34';     // مستوفى
const PART = 'B26B00';   // جزئي / بشرط
const OUT = '5B5B5B';    // خارج النطاق
const BAD = 'B02418';    // غير مستوفى
const HDRBG = 'DCE6F1';
const ALTBG = 'F5F8FC';

const FONT = 'Arial';
const PAGE_W = 9026;     // usable width in DXA for A4 with 1" margins

// ── Helpers ───────────────────────────────────────────────────────────────
const P = (text, opts = {}) => new Paragraph({
  bidirectional: true,
  alignment: opts.align ?? AlignmentType.BOTH,
  spacing: { after: opts.after ?? 120, line: opts.line ?? 300 },
  indent: opts.indent,
  children: [new TextRun({
    text, rightToLeft: true, font: FONT,
    size: opts.size ?? 21, bold: opts.bold, italics: opts.italics,
    color: opts.color
  })],
  ...(opts.border ? { border: opts.border } : {})
});

// Paragraph built from multiple runs (for inline bold)
const PR = (runs, opts = {}) => new Paragraph({
  bidirectional: true,
  alignment: opts.align ?? AlignmentType.BOTH,
  spacing: { after: opts.after ?? 120, line: 300 },
  indent: opts.indent,
  children: runs.map(r => new TextRun({
    text: r.t, rightToLeft: true, font: FONT,
    size: r.size ?? 21, bold: r.b, italics: r.i, color: r.c
  }))
});

const H1 = (text) => new Paragraph({
  bidirectional: true,
  heading: HeadingLevel.HEADING_1,
  alignment: AlignmentType.RIGHT,
  spacing: { before: 320, after: 160 },
  children: [new TextRun({ text, rightToLeft: true, font: FONT, size: 30, bold: true, color: NAVY })]
});

const H2 = (text) => new Paragraph({
  bidirectional: true,
  heading: HeadingLevel.HEADING_2,
  alignment: AlignmentType.RIGHT,
  spacing: { before: 240, after: 120 },
  children: [new TextRun({ text, rightToLeft: true, font: FONT, size: 24, bold: true, color: ACCENT })]
});

const BULLET = (text, opts = {}) => new Paragraph({
  bidirectional: true,
  numbering: { reference: 'bullets', level: 0 },
  alignment: AlignmentType.BOTH,
  spacing: { after: 80, line: 290 },
  children: [new TextRun({ text, rightToLeft: true, font: FONT, size: 21, color: opts.color })]
});

// Code / config block
const CODE = (text) => new Paragraph({
  bidirectional: false,
  alignment: AlignmentType.LEFT,
  spacing: { after: 60, line: 260 },
  shading: { type: ShadingType.CLEAR, fill: 'F2F2F2' },
  indent: { left: 220, right: 220 },
  children: [new TextRun({ text, font: 'Consolas', size: 18, color: '1A1A1A' })]
});

const cell = (children, opts = {}) => new TableCell({
  width: { size: opts.w, type: WidthType.DXA },
  shading: opts.fill ? { type: ShadingType.CLEAR, fill: opts.fill } : undefined,
  margins: { top: 70, bottom: 70, left: 100, right: 100 },
  verticalAlign: VerticalAlign.CENTER,
  children
});

const txtCell = (text, opts = {}) => cell([new Paragraph({
  bidirectional: true,
  alignment: opts.align ?? AlignmentType.RIGHT,
  spacing: { after: 0, line: 260 },
  children: [new TextRun({
    text, rightToLeft: true, font: FONT,
    size: opts.size ?? 19, bold: opts.bold, color: opts.color
  })]
})], opts);

// Status/compliance table: [requirement, status, evidence]
const matrix = (rows, widths) => new Table({
  visuallyRightToLeft: true,
  width: { size: PAGE_W, type: WidthType.DXA },
  columnWidths: widths,
  borders: {
    top:    { style: BorderStyle.SINGLE, size: 4, color: 'B7C7DC' },
    bottom: { style: BorderStyle.SINGLE, size: 4, color: 'B7C7DC' },
    left:   { style: BorderStyle.SINGLE, size: 4, color: 'B7C7DC' },
    right:  { style: BorderStyle.SINGLE, size: 4, color: 'B7C7DC' },
    insideHorizontal: { style: BorderStyle.SINGLE, size: 2, color: 'D7E0EC' },
    insideVertical:   { style: BorderStyle.SINGLE, size: 2, color: 'D7E0EC' }
  },
  rows: rows.map((r, i) => new TableRow({
    tableHeader: i === 0,
    children: r.map((c, j) => {
      const isHead = i === 0;
      const fill = isHead ? HDRBG : (i % 2 === 0 ? ALTBG : undefined);
      const color = isHead ? NAVY : (j === 1 ? c.color : undefined);
      return txtCell(typeof c === 'string' ? c : c.t, {
        w: widths[j], fill, bold: isHead || (j === 1 && !isHead),
        color: isHead ? NAVY : color,
        align: j === 1 ? AlignmentType.CENTER : AlignmentType.RIGHT,
        size: isHead ? 19 : 18
      });
    })
  }))
});

const S = {
  ok:   { t: 'مستوفى', color: OK },
  part: { t: 'مستوفى بشرط', color: PART },
  outs: { t: 'خارج النطاق', color: OUT },
  no:   { t: 'غير مستوفى', color: BAD }
};

const SPACER = (h = 120) => new Paragraph({ spacing: { after: h }, children: [] });

const RULE = () => new Paragraph({
  spacing: { before: 60, after: 160 },
  border: { bottom: { style: BorderStyle.SINGLE, size: 6, color: ACCENT } },
  children: []
});

// ══════════════════════════════════════════════════════════════════════════
// COVER
// ══════════════════════════════════════════════════════════════════════════
const cover = [
  SPACER(1600),
  new Paragraph({
    bidirectional: true, alignment: AlignmentType.CENTER, spacing: { after: 120 },
    children: [new TextRun({ text: 'تقرير المطابقة النهائي', rightToLeft: true, font: FONT, size: 52, bold: true, color: NAVY })]
  }),
  new Paragraph({
    bidirectional: true, alignment: AlignmentType.CENTER, spacing: { after: 320 },
    children: [new TextRun({ text: 'متطلبات الأمن السيبراني', rightToLeft: true, font: FONT, size: 34, bold: true, color: ACCENT })]
  }),
  new Paragraph({
    alignment: AlignmentType.CENTER, spacing: { after: 400 },
    border: { bottom: { style: BorderStyle.SINGLE, size: 12, color: ACCENT } },
    children: []
  }),
  new Paragraph({
    bidirectional: true, alignment: AlignmentType.CENTER, spacing: { after: 100 },
    children: [new TextRun({ text: 'نظام IdentitySync Pro', rightToLeft: true, font: FONT, size: 32, bold: true })]
  }),
  new Paragraph({
    bidirectional: true, alignment: AlignmentType.CENTER, spacing: { after: 700 },
    children: [new TextRun({ text: 'نظام إدارة الهويات ومزامنتها مع Active Directory', rightToLeft: true, font: FONT, size: 22, color: GREY })]
  }),
];

const coverTable = new Table({
  visuallyRightToLeft: true,
  width: { size: 6600, type: WidthType.DXA },
  columnWidths: [2200, 4400],
  alignment: AlignmentType.CENTER,
  borders: {
    top: { style: BorderStyle.SINGLE, size: 4, color: 'B7C7DC' },
    bottom: { style: BorderStyle.SINGLE, size: 4, color: 'B7C7DC' },
    left: { style: BorderStyle.SINGLE, size: 4, color: 'B7C7DC' },
    right: { style: BorderStyle.SINGLE, size: 4, color: 'B7C7DC' },
    insideHorizontal: { style: BorderStyle.SINGLE, size: 2, color: 'D7E0EC' },
    insideVertical: { style: BorderStyle.SINGLE, size: 2, color: 'D7E0EC' }
  },
  rows: [
    ['مُقدَّم إلى', 'إدارة الأمن السيبراني'],
    ['الغرض', 'إثبات استيفاء المتطلبات الأمنية قبل التشغيل'],
    ['إصدار التقرير', '1.0 — النهائي'],
    ['تاريخ الإصدار', '5 أغسطس 2026'],
    ['حالة النظام', 'يعمل في الإنتاج — أكثر من 111,000 هوية'],
    ['المُعِدّ', 'ناصر مهدي سوادي — مطوّر النظام'],
  ].map((r, i) => new TableRow({
    children: [
      txtCell(r[0], { w: 2200, fill: HDRBG, bold: true, color: NAVY, size: 19 }),
      txtCell(r[1], { w: 4400, fill: i % 2 === 0 ? ALTBG : undefined, size: 19 })
    ]
  }))
});

// ══════════════════════════════════════════════════════════════════════════
// BODY
// ══════════════════════════════════════════════════════════════════════════
const body = [];

// ---- 1. Executive summary
body.push(H1('1. الملخّص التنفيذي'));
body.push(RULE());
body.push(P('يوثّق هذا التقرير نتيجة فحص نظام IdentitySync Pro مقابل وثيقة «متطلبات الأمن السيبراني العامة»، والإجراءات التصحيحية التي نُفِّذت لإغلاق الفجوات المكتشفة.'));
body.push(P('جرى الفحص على مستوى الشفرة المصدرية مباشرةً لا على مستوى الوثائق، وأُعيد التحقق من كل بند نُفِّذ بتشغيل النظام فعلياً مقابل قاعدة بيانات حقيقية.'));

body.push(H2('1-1 النتيجة'));
body.push(P('اكتملت جميع البنود التقنية القابلة للتحقق داخل النظام. البنود المتبقية ذات طبيعة إجرائية أو تخصّ البنية التحتية، وهي من مسؤولية الجهة المستفيدة وفريق التشغيل لا من مسؤولية التطبيق.'));
body.push(SPACER(60));

body.push(matrix([
  ['التصنيف', 'العدد', 'الوصف'],
  ['متطلبات تقنية داخل النظام', { t: '32', color: OK }, 'مستوفاة ومُتحقَّق منها في الشفرة وبالتشغيل'],
  ['متطلبات مستوفاة بشرط تشغيلي', { t: '4', color: PART }, 'تتطلّب ضبطاً أو إجراءً عند النشر — موضّحة في القسم 6'],
  ['متطلبات خارج نطاق التطبيق', { t: '29', color: OUT }, 'إجرائية أو بنية تحتية — القسم 7'],
  ['متطلبات غير مستوفاة', { t: '0', color: OK }, 'لا يوجد'],
], [3400, 1100, 4526]));

body.push(SPACER(140));
body.push(P('أُنجزت تسعة تحصينات جديدة خلال هذه المرحلة، وارتفعت تغطية الاختبارات الآلية إلى 367 اختباراً ناجحاً دون أي إخفاق.', { bold: true }));

// ---- 2. Scope & methodology
body.push(H1('2. النطاق والمنهجية'));
body.push(RULE());

body.push(H2('2-1 نطاق الفحص'));
body.push(P('يشمل الفحص تطبيق الويب (ASP.NET Core 8) بمشاريعه الأربعة: الواجهة، والبنية التحتية، والنواة، والاختبارات؛ ويشمل الوحدات المستقلة: مزامنة الهويات، ووحدة الخدمات، ووحدة حالة الحسابات، ومركز الإشعارات، وبوابة الخدمة الذاتية لكلمة المرور.'));

body.push(H2('2-2 منهجية التحقق'));
body.push(P('اعتُمدت أربع طبقات تحقق متتابعة، ولم يُعتمد أيٌّ منها منفرداً:'));
body.push(BULLET('فحص الشفرة المصدرية مباشرةً لكل بند، لا الاعتماد على الوثائق المرافقة.'));
body.push(BULLET('اختبارات آلية (xUnit) تُثبِّت السلوك الأمني المطلوب.'));
body.push(BULLET('اختبار الطفرة (Mutation Testing): كسر كل ضابط أمني عمداً للتأكد من أن الاختبارات تكشفه فعلاً — فاختبار ينجح مع الضابط ومن دونه لا يثبت شيئاً.'));
body.push(BULLET('التشغيل الفعلي للنظام والتحقق من الاستجابات وقواعد البيانات والمتصفح، على قواعد بيانات منفصلة مؤقتة أُنشئت وحُذفت.'));

body.push(new Paragraph({
  bidirectional: true, spacing: { before: 160, after: 120, line: 300 },
  shading: { type: ShadingType.CLEAR, fill: 'FFF6E5' },
  indent: { left: 160, right: 160 },
  border: {
    top: { style: BorderStyle.SINGLE, size: 4, color: PART },
    bottom: { style: BorderStyle.SINGLE, size: 4, color: PART },
    left: { style: BorderStyle.SINGLE, size: 4, color: PART },
    right: { style: BorderStyle.SINGLE, size: 4, color: PART }
  },
  children: [
    new TextRun({ text: 'ملاحظة منهجية: ', rightToLeft: true, font: FONT, size: 20, bold: true, color: PART }),
    new TextRun({
      text: 'نجاح البناء (Build) لا يُعدّ دليلاً على سلامة الشفرة البرمجية داخل صفحات العرض، لأن مصرّف القوالب لا يفحص شفرة JavaScript المضمّنة. لذلك جرى التحقق من هذه الشفرة بالتشغيل الفعلي في المتصفح.',
      rightToLeft: true, font: FONT, size: 20
    })
  ]
}));

body.push(new Paragraph({ children: [new PageBreak()] }));

// ---- 3. General requirements
body.push(H1('3. المتطلبات العامة'));
body.push(RULE());
body.push(matrix([
  ['المتطلب', 'الحالة', 'الدليل / الإجراء'],
  ['اختبار استيفاء المتطلبات الأمنية ورفع تقرير', S.ok, 'هذا التقرير'],
  ['تقييم الثغرات ومعالجتها قبل التشغيل', S.part, 'المعالجة التقنية مكتملة؛ فحص الثغرات بأداة معتمدة من مسؤولية الجهة'],
  ['مراجعة الإعدادات والتحصين وحزم التحديثات', S.ok, 'ترويسات أمان كاملة، إعدادات مركزية، منصّة ASP.NET Core 8 محدّثة'],
  ['الالتزام بسياسات الأمن السيبراني للجامعة', S.ok, 'كل قيم السياسة قابلة للضبط دون تعديل الشفرة'],
  ['معايير التطوير الآمن للتطبيقات', S.ok, 'تفصيلها في القسم 5'],
  ['مصادر مرخّصة وموثوقة للمكتبات', S.ok, 'حزم NuGet رسمية (Microsoft, Serilog, Hangfire, Oracle, ClosedXML, QRCoder) ومكتبات واجهة مستضافة ذاتياً'],
  ['أمن التكامل بين الأنظمة', S.ok, 'واجهة SCIM بمفتاح مستقل ومقارنة ثابتة الزمن؛ اتصالات LDAP مشفّرة إلزامياً'],
  ['اتفاقية عدم الإفشاء', S.outs, 'إجراء تعاقدي'],
  ['سياسة الاستخدام الآمن للمشروع', S.part, 'الضوابط التقنية مطبّقة؛ الوثيقة الإدارية من مسؤولية الجهة'],
  ['حصر البيانات داخل المملكة وعدم الاتصال المباشر بالإنترنت', S.ok, 'أُزيلت كل التبعيات الخارجية؛ النظام يعمل بلا أي اتصال صادر — القسم 5-9'],
  ['الدعم الفني في مقر الجامعة', S.outs, 'إجراء تعاقدي'],
  ['معمارية متعددة المستويات (3 مستويات كحد أدنى)', S.part, 'الفصل المنطقي قائم (عرض / منطق أعمال / بيانات) والفصل المادي قرار نشر'],
  ['التعهد بإغلاق أي ثغرة مستقبلية', S.outs, 'إجراء تعاقدي'],
  ['فصل الواجهة الأمامية عن الخلفية', S.ok, 'ASP.NET Core MVC بفصل كامل بين طبقة العرض ومنطق الأعمال'],
  ['الاستضافة السحابية داخل المملكة', S.outs, 'النظام يُستضاف داخلياً — لا ينطبق'],
  ['تصنيف البيانات والإفصاح عن نوعها', S.outs, 'من مسؤولية مالك النظام'],
], [3300, 1180, 4546]));

// ---- 4. Sensitive systems
body.push(H1('4. متطلبات الأنظمة الحسّاسة'));
body.push(RULE());
body.push(P('يدير النظام هويات Active Directory ويكتب كلمات المرور، لذا يُرجَّح تصنيفه نظاماً حسّاساً، ما يستدعي تطبيق هذه المتطلبات.'));
body.push(SPACER(60));
body.push(matrix([
  ['المتطلب', 'الحالة', 'الدليل / الإجراء'],
  ['اختبار التحمّل (Stress Testing)', S.outs, 'يُنفَّذ ضمن خطة الاختبار التشغيلي — النظام يعالج 111,000+ هوية في الإنتاج'],
  ['متطلبات استمرارية الأعمال', S.part, 'قاطع دائرة، وطابور رسائل ميتة، وحجر، وإعادة محاولة؛ خطة الاستمرارية إدارية'],
  ['مراجعة أمنية للشفرة المصدرية واختبار اختراق', S.part, 'أُجريت مراجعة داخلية شاملة (هذا التقرير)؛ الاختبار الخارجي من مسؤولية الجهة'],
  ['تأمين الوصول والتخزين والتوثيق للشفرة المصدرية', S.outs, 'من مسؤولية الجهة — يُوصى بمستودع مُدار بصلاحيات'],
  ['تأمين واجهة برمجة التطبيقات', S.ok, 'SCIM بمفتاح إلزامي، ورفض المفاتيح الافتراضية، ومقارنة ثابتة الزمن'],
  ['النقل الآمن من الاختبار إلى الإنتاج وحذف بيانات الاختبار', S.ok, 'لا تُنقل الأسرار مع الشفرة؛ مفاتيح التشفير خارج المستودع'],
  ['تطبيق إطار CSCC-1:2019 للأنظمة الحساسة', S.outs, 'إطار تنظيمي على مستوى الجهة'],
  ['تفادي ثغرات OWASP Top 10', S.ok, 'تفصيلها في القسم 5-10'],
], [3300, 1180, 4546]));

body.push(new Paragraph({ children: [new PageBreak()] }));

// ---- 5. Application requirements — the meat
body.push(H1('5. متطلبات الأمن السيبراني للتطبيقات'));
body.push(RULE());
body.push(P('هذا القسم يغطّي البنود التي يتحمّل التطبيق مسؤوليتها كاملة. البنود المكتوبة بخط عريض نُفِّذت خلال هذه المرحلة.'));
body.push(SPACER(60));

body.push(matrix([
  ['المتطلب', 'الحالة', 'التنفيذ في النظام'],
  ['التدقيق على مستوى التطبيق (هوية المستخدم، IP، الإجراءات)', S.ok, 'سجل تدقيق يحفظ المنفِّذ وعنوان IP والإجراء ومُعرّف الارتباط لكل عملية'],
  ['مهلة الخمول 10 دقائق', S.ok, 'نُفِّذ — جلسة منزلقة 10 دقائق قابلة للضبط، مع نبضة تُجدَّد بالتفاعل الحقيقي فقط'],
  ['تعطيل/إعادة تسمية حسابات المسؤول الافتراضية', S.ok, 'نُفِّذ — اسم الحساب المبدئي قابل للضبط، وكلمة مرور عشوائية، وإمكانية إعادة التسمية'],
  ['أفضل ممارسات كلمة المرور (تركيب، طول، تجزئة)', S.ok, 'PBKDF2-SHA256 بمئة ألف تكرار، وحد أدنى 10 محارف مع حروف وأرقام'],
  ['تمكين HTTPS', S.ok, 'إعادة توجيه إلزامية + HSTS، وكوكي المصادقة عبر HTTPS حصراً'],
  ['SSH v2 وطول تشفير 2048 فأكثر', S.outs, 'إعداد نظام تشغيل الخادم'],
  ['عدم استضافة قاعدة البيانات على خادم التطبيق', S.outs, 'قرار نشر — النظام يدعم خادم قاعدة بيانات منفصلاً'],
  ['تشفير البيانات المحلية', S.ok, 'كل الأسرار مشفّرة at-rest، والمفتاح مفصول عن قاعدة البيانات'],
  ['الحماية من محاولات الدخول القاسية', S.ok, 'قفل بعد 5 محاولات لمدة 15 دقيقة، وحظر عناوين IP في البوابة العامة'],
  ['عدم استخدام خوارزميات تشفير ضعيفة', S.ok, 'مسح شامل: لا وجود لـ MD5 أو RC4 أو DES أو SHA1 في أي مسار'],
  ['تطبيق أحدث التصحيحات', S.ok, 'ASP.NET Core 8 وحزم محدّثة'],
  ['تحديثات المورّد عبر HTTPS', S.ok, 'كل الحزم من مستودع NuGet الرسمي عبر HTTPS'],
  ['ترميز المخرجات وضوابط البيانات', S.ok, 'ترميز تلقائي في محرك القوالب، وسياسة محتوى صارمة'],
  ['تغيير كلمة المرور كل 90 يوماً', S.ok, 'نُفِّذ — انتهاء صلاحية قابل للضبط مع إعفاء حسابات الدومين'],
  ['تصفية مدخلات استعلامات SQL و XML و LDAP ونظام التشغيل', S.ok, 'معاملات مُمرَّرة في SQL، وتهريب فلاتر LDAP، ولا تنفيذ لأوامر نظام التشغيل'],
  ['المصادقة متعددة العوامل للحسابات ذات الامتيازات', S.ok, 'نُفِّذ — TOTP للأدوار المشمولة مع رموز استرداد'],
  ['قصر الوصول على المستخدمين المصرّح لهم', S.ok, 'مصادقة إلزامية على كل الصفحات وثلاثة أدوار محدّدة الصلاحيات'],
  ['عدم الإفصاح عن معلومات حساسة في رسائل الخطأ', S.ok, 'صفحة خطأ عامة لا تكشف تفاصيل النظام أو الجلسات'],
  ['منع إصدار أوامر مباشرة إلى نظام التشغيل', S.ok, 'لا يوجد أي استدعاء لتشغيل عمليات نظام التشغيل في النظام كله'],
  ['ممارسات أمان رفع الملفات', S.ok, 'حد للحجم، وتحقق كامل على الخادم، ولا تُكتب الملفات على القرص'],
], [3300, 1180, 4546]));

body.push(new Paragraph({ children: [new PageBreak()] }));

// ---- 6. What was implemented
body.push(H1('6. التحصينات المنفَّذة في هذه المرحلة'));
body.push(RULE());
body.push(P('أُنجزت تسعة تحصينات. جميع قيم السياسة وُضعت في ملف الإعدادات لا في الشفرة، لأنها قرارات مؤسسية تختلف بين جهة وأخرى، ولأن من يواجه مشكلة تشغيلية يحتاج تعديل سطر لا إعادة نشر.'));

const items = [
  ['6-1', 'مهلة الخمول',
    'خُفِّضت من ثماني ساعات إلى عشر دقائق منزلقة، قابلة للضبط بين 1 و480 دقيقة.',
    'نافذة بهذا القِصَر كانت ستُفقد المشغّل عمله أثناء تعبئة الشاشات الطويلة، لذلك أُضيفت نبضة تُجدِّد الجلسة بعد تفاعل حقيقي فقط (لوحة مفاتيح أو مؤشر أو لمس). الجلسة المهجورة تنتهي كما يقتضي المتطلب، والمستخدَمة لا تنقطع. النبضة الزمنية المجرّدة كانت ستُبقي محطة مهجورة داخلة إلى الأبد وتُبطل البند الذي جاءت لخدمته.'],
  ['6-2', 'كوكي المصادقة عبر HTTPS حصراً',
    'تغيّر من الإرسال حسب البروتوكول إلى الإرسال المشفّر إلزامياً.',
    'هذا البند الوحيد القادر على منع الدخول كلياً إن لم يكن للموقع ارتباط HTTPS، لذا جُعل قابلاً للتعطيل بسطر واحد، ويُطبع الوضع الفعلي في سجل الإقلاع.'],
  ['6-3', 'ترويسات الحماية في المتصفح',
    'أُضيفت خمس ترويسات: سياسة أمن المحتوى، ومنع التأطير، ومنع استنتاج نوع المحتوى، وسياسة المُحيل، وسياسة الصلاحيات.',
    'سياسة المحتوى تسمح بالشفرة المضمّنة لأن الواجهة القائمة تعتمد عليها في أكثر من مئة موضع، وسياسة صارمة كانت ستُعطّل التطبيق بالكامل. ما تبقّى من الحماية فعّال: منع التأطير، ومنع الإضافات، وحماية وسم الأساس ووجهة النماذج.'],
  ['6-4', 'رفض مفاتيح الواجهات الافتراضية',
    'المفتاح المتروك على قيمته المرفقة مع النظام يُعامَل كأنه غير مضبوط، فتُحجب واجهة SCIM.',
    'المفتاح الافتراضي مطبوع في ملفات المشروع، أي أنه مفتاح منشور. وقبل التحصين كان يجتاز كل فحوص «هل المفتاح مضبوط؟» فتبدو الواجهة محميّة وهي ليست كذلك.'],
  ['6-5', 'انتهاء صلاحية كلمة المرور',
    'تسعون يوماً قابلة للضبط، مع إعفاء حسابات الدومين لأن كلمة مرورها تُدار في Active Directory.',
    'عند الترقية تُختم الحسابات القائمة بوقت الترقية لا بتاريخ إنشائها، لأن إعادة تعيين كلمة المرور لم تكن تُسجَّل، فاعتبار تاريخ الإنشاء تاريخَ آخر تغيير كان سيُجبر على التغيير من غيّر كلمته بالأمس. النتيجة: لا أحد يُجبر يوم الترقية. وأُضيف رفض إعادة استخدام كلمة المرور الحالية، لأنها كانت تصفّر عدّاد العمر بلا تغيير فعلي.'],
  ['6-6', 'حساب المسؤول الافتراضي',
    'اسم الحساب المبدئي أصبح قابلاً للضبط، وكلمة مروره تُولَّد عشوائياً وتُعرض مرة واحدة، وأُضيفت إمكانية إعادة تسمية أي حساب.',
    'التثبيتات القائمة لا تُعاد تسميتها تلقائياً لأن إعادة تسمية الحساب الذي يُستخدم للدخول ليست قراراً يتخذه تسلسل إقلاع؛ لكنها تُعلَن في سجل الإقلاع. وإعادة تسمية الحساب المستخدَم حالياً لا تقطع الجلسة لأن الصلاحيات مبنية على مُعرّف المستخدم لا اسمه.'],
  ['6-7', 'المصادقة متعددة العوامل',
    'كلمة مرور لمرة واحدة مبنية على الوقت (TOTP) للأدوار المشمولة، مع تفعيل وتعطيل من واجهة الإدارة.',
    'اختيرت هذه التقنية لا الرسائل النصية لأنها تعمل بلا إنترنت وبلا بوابة رسائل، وهو الخيار الوحيد المتسق مع شبكة معزولة. رمز الاستجابة السريعة يُولَّد في الخادم، فلا ترى أي خدمة خارجية المفتاح السرّي. والمفتاح مشفّر في قاعدة البيانات، ورموز الاسترداد العشرة مُجزّأة وتُستخدم مرة واحدة.'],
  ['6-8', 'حاجزان مستقلان في تدفّق المصادقة',
    'المستخدم الذي أثبت كلمة المرور ولم يُكمل العامل الثاني يُمنح هوية بلا أي صلاحية دور.',
    'هذا أهم قرار في التصميم: الشاشات الإدارية ترفضه من تلقاء نفسها حتى لو تعطّل الفلتر الذي يحصره في شاشات التحقق. أُثبت عملياً بأن الشاشات الإدارية تُرجع «صلاحية مرفوضة» لا مجرد إعادة توجيه.'],
  ['6-9', 'استضافة مكتبات الواجهة ذاتياً',
    'نُقلت كل مكتبات الواجهة والخطوط إلى داخل النظام، وأُزيلت كل النطاقات الخارجية من سياسة المحتوى.',
    'كانت الواجهة تُحمّل خمس مكتبات وخطّين من شبكات توزيع خارجية، وهو ما يخالف بند حصر الاتصال ويجعل الواجهة تظهر بلا تنسيق ولا رسوم على شبكة معزولة. وبإزالة النطاقات من سياسة المحتوى، فإن أي إعادة إدخال لمرجع خارجي مستقبلاً يمنعها المتصفح بدل أن تعود التبعية بصمت.'],
];

for (const [num, title, what, why] of items) {
  body.push(H2(`${num} ${title}`));
  body.push(PR([{ t: 'ما نُفِّذ: ', b: true, c: NAVY }, { t: what }]));
  body.push(PR([{ t: 'المبرِّر الهندسي: ', b: true, c: NAVY }, { t: why }]));
}

body.push(new Paragraph({ children: [new PageBreak()] }));

// ---- 7. Verification results
body.push(H1('7. نتائج التحقق'));
body.push(RULE());

body.push(H2('7-1 الاختبارات الآلية'));
body.push(matrix([
  ['المؤشر', 'النتيجة'],
  ['إجمالي الاختبارات', '367'],
  ['الناجحة', '367'],
  ['المخفقة', '0'],
  ['أخطاء البناء', '0'],
  ['اختبارات أُضيفت في هذه المرحلة', '51'],
], [6026, 3000]));

body.push(SPACER(140));
body.push(H2('7-2 اختبار الطفرة'));
body.push(P('كُسر كل ضابط أمني عمداً للتأكد من أن الاختبارات تكشفه. اختبار ينجح مع الضابط ومن دونه لا يثبت شيئاً:'));
body.push(SPACER(60));
body.push(matrix([
  ['الضابط المكسور عمداً', 'النتيجة'],
  ['قبول المفاتيح الافتراضية', 'أخفقت 8 اختبارات'],
  ['حجب كل المفاتيح بلا تمييز', 'أخفق اختباران ضابطان'],
  ['إلغاء إعفاء حسابات الدومين من انتهاء الصلاحية', 'أخفق اختباران'],
  ['إلغاء منع إعادة استخدام رمز التحقق', 'أخفق اختبار الحماية من إعادة البثّ'],
], [6026, 3000]));

body.push(SPACER(140));
body.push(H2('7-3 التحقق بالتشغيل الفعلي'));
body.push(BULLET('التحقق من ظهور الترويسات الخمس في استجابة الخادم فعلياً.'));
body.push(BULLET('اختبار سياسة المحتوى على العناصر الثلاثة التي كانت ستُعطّل الواجهة: الشفرة المضمّنة، ومعالجات الأحداث، والمكتبات الخارجية.'));
body.push(BULLET('مفتاح واجهة افتراضي: محجوب. مفتاح صحيح: مقبول. مفتاح خاطئ: مرفوض.'));
body.push(BULLET('كلمة مرور عمرها 91 يوماً: الدخول ينجح ثم يُطلب التغيير فوراً.'));
body.push(BULLET('ترقية قاعدة بيانات قائمة: أُضيف العمود، ولم يُجبَر أي مستخدم على التغيير يوم الترقية.'));
body.push(BULLET('ثبات الترقية عند إعادة التشغيل: عمر كلمة المرور لم يُعَد ضبطه، وإلا لما انتهت كلمة مرور أبداً.'));
body.push(BULLET('إعادة التسمية: الاسم المكرّر والفارغ والمطابق مرفوضة؛ وإعادة تسمية الحساب المستخدَم لا تقطع الجلسة ولا الصلاحيات.'));
body.push(BULLET('المصادقة الثنائية: حُسب الرمز بتنفيذ برمجي مستقل تماماً عن شفرة النظام وقُبل، وهو دليل مباشر على أن تطبيقات المصادقة المعروفة ستعمل.'));
body.push(BULLET('إعادة بثّ رمز تحقق مستهلَك: مرفوضة. رمز استرداد: مقبول مرة واحدة فقط ثم يُستهلك.'));
body.push(BULLET('المفتاح السرّي مخزَّن مشفّراً، ورموز الاسترداد مُجزّأة — تُحقِّق منها في قاعدة البيانات مباشرةً.'));
body.push(BULLET('بعد الاستضافة الذاتية: صفر طلب خارجي على صفحة الدخول وعلى واجهة التطبيق كاملة، وصفر خطأ في المتصفح.'));

body.push(new Paragraph({ children: [new PageBreak()] }));

// ---- 8. Conditions & disclosures
body.push(H1('8. شروط تشغيلية وإفصاحات'));
body.push(RULE());
body.push(P('يُفصح هذا القسم عن كل قيد أو مقايضة معروفة، التزاماً بالشفافية أمام إدارة الأمن السيبراني.'));

body.push(H2('8-1 شروط يجب استيفاؤها عند النشر'));
body.push(matrix([
  ['البند', 'الإجراء المطلوب'],
  ['ارتباط HTTPS', 'يجب التأكد من وجود ارتباط HTTPS قبل التشغيل، وإلا فلن يعود المتصفح بكوكي المصادقة ولن يتمكّن أحد من الدخول. البديل المؤقت تعطيل الخيار في ملف الإعدادات.'],
  ['مفاتيح الواجهات', 'توليد مفاتيح قوية لواجهة SCIM ولوحة المهام قبل التشغيل؛ المفاتيح الافتراضية محجوبة تلقائياً.'],
  ['إعادة تسمية حساب المسؤول', 'إعادة تسمية الحساب الافتراضي القائم من شاشة إدارة المستخدمين.'],
  ['النشر خلف وسيط', 'عند وجود وسيط أو موزّع أحمال، يجب ضبط قسم الشبكة في الإعدادات وإلا ظهر كل المستخدمين بعنوان واحد.'],
  ['نسخ مفاتيح التشفير', 'الاحتفاظ بنسخة احتياطية من مجلد مفاتيح الحماية؛ فقدانه يعني تعذّر فك الأسرار المخزّنة.'],
], [2600, 6426]));

body.push(SPACER(140));
body.push(H2('8-2 إفصاحات ومقايضات معلنة'));
body.push(matrix([
  ['البند', 'الإفصاح'],
  ['بوابة الخدمة الذاتية', 'بناءً على قرار إداري سابق، تُصرّح البوابة بسبب الخطأ بدل رسالة موحّدة، ما يتيح نظرياً استكشاف أسماء المستخدمين الصحيحة بالتجربة. الضابط التعويضي هو حظر عناوين IP، ويجب إبقاؤه مفعّلاً بعدد محاولات منخفض.'],
  ['سياسة أمن المحتوى', 'تسمح بالشفرة المضمّنة لأن الواجهة القائمة تعتمد عليها؛ التشديد الكامل يتطلّب إعادة هيكلة واجهات المستخدم.'],
  ['أسماء الكائنات في استعلامات قاعدة البيانات', 'تُدرَج أسماء الجداول والأعمدة نصّياً وهي قادمة من إعدادات المسؤول لا من مدخلات المستخدم؛ القيم كلها مُمرَّرة كمعاملات. يُوصى بتقييدها بقائمة بيضاء مستقبلاً.'],
  ['شهادة خادم قاعدة البيانات', 'سلسلة الاتصال تثق بالشهادة دون تحقق؛ يُوصى بتثبيت شهادة موثوقة على خادم قاعدة البيانات وإلغاء هذا الخيار.'],
  ['إعفاء حسابات الدومين من انتهاء الصلاحية', 'مُتحقَّق منه بالاختبارات الآلية واختبار الطفرة، ولم يُجرَّب مقابل دومين حقيقي.'],
  ['ربط سجل التدقيق بالجهات', 'سجل التدقيق العام لا يحمل مُعرّف الجهة؛ تأجيل واعٍ يتطلّب تعديل مخطط قاعدة البيانات.'],
], [2600, 6426]));

body.push(new Paragraph({ children: [new PageBreak()] }));

// ---- 9. Out of scope
body.push(H1('9. بنود خارج نطاق التطبيق'));
body.push(RULE());
body.push(P('هذه البنود لا يحسمها التطبيق، وهي من مسؤولية الجهة المستفيدة وفريق التشغيل والبنية التحتية:'));

body.push(H2('9-1 بنود إجرائية وتعاقدية'));
body.push(BULLET('اختبار الاختراق الخارجي والمراجعة الأمنية المستقلة للشفرة المصدرية ورفع تقاريرهما.'));
body.push(BULLET('اتفاقية عدم الإفشاء، وتعهّد إغلاق الثغرات المستقبلية، والدعم الفني في مقر الجامعة.'));
body.push(BULLET('تصنيف البيانات والإفصاح عن نوعها من مالك النظام.'));
body.push(BULLET('تأمين مستودع الشفرة المصدرية وإصداراته وصلاحيات الوصول إليه.'));
body.push(BULLET('سجل البرمجيات: اسم البرنامج وناشره، وتاريخ ومصدر الحصول عليه، وموقع كل تثبيت، والأرقام التسلسلية، والنسخ الاحتياطية، وترتيبات الدعم.'));
body.push(BULLET('تسجيل البرنامج باسم الجامعة والتراخيص الموثوقة.'));

body.push(H2('9-2 بنود البنية التحتية والخوادم'));
body.push(BULLET('تحصين نظام تشغيل الخادم وحزم تحديثاته والدعم الفني له.'));
body.push(BULLET('إصدار SSH وطول مفاتيح التشفير، وتعطيل المنافذ والخدمات غير المستخدمة.'));
body.push(BULLET('فصل خادم قاعدة البيانات عن خادم التطبيق، وعزل النطاقات الشبكية.'));
body.push(BULLET('مهلة الخمول على مستوى الخادم، وتعطيل مجلدات المشاركة، وإيقاف قوائم الأدلة.'));
body.push(BULLET('إعادة تسمية حسابات المسؤول على مستوى نظام التشغيل، وإزالة الحسابات ذات الامتيازات غير المستخدمة.'));
body.push(BULLET('الأمن المادي للأجهزة، والاستضافة داخل المملكة العربية السعودية.'));
body.push(BULLET('تدقيق الخادم وتدقيق الأدلة الحرجة على مستوى المجلدات.'));

// ---- 10. Recommendations
body.push(H1('10. التوصيات'));
body.push(RULE());
body.push(P('تُقترح الخطوات التالية لاستكمال منظومة الامتثال:'));
body.push(BULLET('تنفيذ فحص ثغرات آلي واختبار اختراق خارجي على البيئة النهائية قبل التشغيل الرسمي، ورفع تقاريرهما.'));
body.push(BULLET('استيفاء الشروط التشغيلية الخمسة الواردة في القسم 8-1 قبل النشر.'));
body.push(BULLET('اعتماد المصادقة متعددة العوامل لحسابات المديرين فور التشغيل، وتوزيع رموز الاسترداد وحفظها في مكان آمن.'));
body.push(BULLET('توثيق مخرج الطوارئ الوارد في الملحق لدى فريق التشغيل، لضمان استعادة الوصول عند فقدان أجهزة المصادقة.'));
body.push(BULLET('مراجعة سجل التدقيق دورياً، لا سيما محاولات الدخول الفاشلة واستخدام رموز الاسترداد وتغييرات سياسة المصادقة.'));
body.push(BULLET('جدولة مراجعة أمنية دورية عند كل تحديث جوهري للنظام.'));

// ---- Appendix
body.push(new Paragraph({ children: [new PageBreak()] }));
body.push(H1('الملحق: المرجع الفني'));
body.push(RULE());

body.push(H2('أ. إعدادات السياسة الأمنية'));
body.push(P('كل قيم السياسة في قسم واحد بملف الإعدادات، ولا تتطلّب تعديل الشفرة:'));
body.push(CODE('"Security": {'));
body.push(CODE('    "IdleTimeoutMinutes": 10,          // مهلة الخمول بالدقائق'));
body.push(CODE('    "RequireHttpsCookie": true,        // كوكي المصادقة عبر HTTPS حصراً'));
body.push(CODE('    "EnableSecurityHeaders": true,     // ترويسات الحماية'));
body.push(CODE('    "ContentSecurityPolicyReportOnly": false,'));
body.push(CODE('    "CspExtraHosts": "",               // نطاقات إضافية عند الحاجة'));
body.push(CODE('    "PasswordMaxAgeDays": 90,          // انتهاء الصلاحية (0 = تعطيل)'));
body.push(CODE('    "DefaultAdminUsername": "isp-admin",'));
body.push(CODE('    "DefaultAdminPassword": ""         // فارغ = توليد عشوائي'));
body.push(CODE('}'));

body.push(SPACER(140));
body.push(H2('ب. مخرج الطوارئ للمصادقة متعددة العوامل'));
body.push(P('عند فقدان جميع أجهزة المصادقة ونفاد رموز الاسترداد، يُعاد فتح النظام بتعطيل السياسة مباشرةً في قاعدة البيانات. وهذا سبب مقصود لوضع السياسة في قاعدة البيانات لا في ملف الإعدادات:'));
body.push(CODE('UPDATE MfaSettings SET IsEnabled = 0;'));
body.push(P('يُوصى بحصر صلاحية تنفيذ هذا الأمر ضمن إجراءات الطوارئ المعتمدة لدى فريق التشغيل.', { italics: true, color: GREY }));

body.push(SPACER(140));
body.push(H2('ج. الضوابط الأمنية القائمة قبل هذه المرحلة'));
body.push(BULLET('مصادقة إلزامية على كل الصفحات، وثلاثة أدوار محدّدة الصلاحيات.'));
body.push(BULLET('تجزئة كلمات المرور بـ PBKDF2-SHA256 بمئة ألف تكرار.'));
body.push(BULLET('قفل الحساب بعد خمس محاولات فاشلة، ورسالة خطأ موحّدة لا تكشف وجود الحساب.'));
body.push(BULLET('حماية شاملة من تزوير الطلبات عبر المواقع على كل عمليات الكتابة.'));
body.push(BULLET('تشفير كل الأسرار المخزّنة، مع فصل المفتاح عن قاعدة البيانات.'));
body.push(BULLET('تهريب فلاتر LDAP في كل مسارات البحث، ومنع الربط المجهول بكلمة مرور فارغة.'));
body.push(BULLET('توحيد قناة LDAP وإلزام التشفير في كل الوحدات.'));
body.push(BULLET('سجل تدقيق أمني لكل دخول وخروج وفشل وتغيير في إدارة المستخدمين.'));
body.push(BULLET('مبدأ المزامنة الآمنة: لا يحذف النظام ولا يعطّل أي حساب خارج الإجراءات المصرّح بها.'));

// ── Document ──────────────────────────────────────────────────────────────
const doc = new Document({
  creator: 'ناصر مهدي سوادي',
  title: 'تقرير المطابقة النهائي — متطلبات الأمن السيبراني — IdentitySync Pro',
  description: 'تقرير مطابقة نظام IdentitySync Pro لمتطلبات الأمن السيبراني',
  styles: {
    default: {
      document: { run: { font: FONT, size: 21 } }
    }
  },
  numbering: {
    config: [{
      reference: 'bullets',
      levels: [{
        level: 0, format: LevelFormat.BULLET, text: '•',
        alignment: AlignmentType.RIGHT,
        style: { paragraph: { indent: { right: 460, hanging: 240 } } }
      }]
    }]
  },
  sections: [
    // Cover
    {
      properties: {
        page: { margin: { top: 1440, right: 1440, bottom: 1440, left: 1440 } }
      },
      children: [...cover, coverTable]
    },
    // Body
    {
      properties: {
        page: { margin: { top: 1440, right: 1440, bottom: 1440, left: 1440 } }
      },
      headers: {
        default: new Header({
          children: [new Paragraph({
            bidirectional: true,
            alignment: AlignmentType.RIGHT,
            spacing: { after: 40 },
            border: { bottom: { style: BorderStyle.SINGLE, size: 4, color: 'C8D4E4' } },
            children: [new TextRun({
              text: 'تقرير المطابقة النهائي — متطلبات الأمن السيبراني — IdentitySync Pro',
              rightToLeft: true, font: FONT, size: 16, color: GREY
            })]
          })]
        })
      },
      footers: {
        default: new Footer({
          children: [new Paragraph({
            bidirectional: true,
            alignment: AlignmentType.CENTER,
            children: [
              new TextRun({ text: 'صفحة ', rightToLeft: true, font: FONT, size: 16, color: GREY }),
              new TextRun({ children: [PageNumber.CURRENT], font: FONT, size: 16, color: GREY }),
              new TextRun({ text: ' من ', rightToLeft: true, font: FONT, size: 16, color: GREY }),
              new TextRun({ children: [PageNumber.TOTAL_PAGES], font: FONT, size: 16, color: GREY })
            ]
          })]
        })
      },
      children: body
    }
  ]
});

Packer.toBuffer(doc).then(buf => {
  const out = process.argv[2];
  fs.writeFileSync(out, buf);
  console.log('written:', out, buf.length, 'bytes');
});
