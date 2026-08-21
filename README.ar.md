# YARP UI

[![NuGet](https://img.shields.io/nuget/v/YA-RP-UI.svg)](https://www.nuget.org/packages/YA-RP-UI)
[![downloads](https://img.shields.io/nuget/dt/YA-RP-UI.svg)](https://www.nuget.org/packages/YA-RP-UI)
[![docker](https://img.shields.io/docker/v/amrfswalha/yarp-ui.svg?label=docker)](https://hub.docker.com/r/amrfswalha/yarp-ui)
[![docker pulls](https://img.shields.io/docker/pulls/amrfswalha/yarp-ui.svg?label=docker%20pulls)](https://hub.docker.com/r/amrfswalha/yarp-ui)

> 🌐 اللغات: [English](README.md) | **العربية** | [Español](README.es.md) | [简体中文](README.zh-CN.md)

واجهة إدارة لـ [YARP](https://microsoft.github.io/reverse-proxy/) (Yet Another Reverse Proxy — بروكسي عكسي آخر). تطبيق واحد يجمع بين **بروكسي عكسي عامل** وغرفة التحكم الخاصة به:

- **خريطة المسارات** (`/`) — كل مسار → مجموعة → وجهة مرسومة كرسم بياني تفاعلي. انقر على أي عقدة لتتبّع سلسلتها الكاملة وفحص إعداداتها؛ ابحث لتظليل النتائج المطابقة.
- **المحرّر** (`/editor`) — إنشاء المسارات والمجموعات والوجهات وتعديلها وحذفها. عند الحفظ يتم التحقق من صحة الإعدادات وتطبيقها على البروكسي قيد التشغيل **دون إعادة تشغيل**، مع تخزينها على القرص.
- **السجلات** (`/logs`) — عرض مباشر للطلبات المُمرَّرة عبر البروكسي (الطريقة، مسار URL، رمز الحالة، المدة، عنوان IP للعميل، المسار، المجموعة، الوجهة المختارة)، الأحدث أولًا مع أعمدة قابلة للفرز. تعمل مرشّحات المسار/المجموعة/الوجهة ومحدد الإطار الزمني على البحث في كامل السجل المحفوظ لا في المخزن المؤقت المباشر فحسب. إضافة إلى لوحة أداء: مخطط لمدد الطلبات عبر الزمن ملوّن حسب فئة الحالة، وبطاقات إحصاءات (المتوسط/P95/الأقصى/نسبة الأخطاء)، وتجميعات لكل مسار.

> **الإصدارات** — هذا المستودع هو **الإصدار المجتمعي**، مجاني بموجب ترخيص Apache-2.0. يضيف إصدار premium منفصل ميزات تجارية فوقه ويُوزَّع بترخيص تجاري. لا يحتوي هذا المستودع أبدًا على كود الإصدار التجاري.

## التوثيق

الأدلة الكاملة موجودة في [ويكي المشروع](https://github.com/advanced-software-solutions/yarp-ui/wiki):

- [البدء](https://github.com/advanced-software-solutions/yarp-ui/wiki/Getting-Started)
- [أوضاع الاستضافة](https://github.com/advanced-software-solutions/yarp-ui/wiki/Hosting-Modes)
- [الإعدادات](https://github.com/advanced-software-solutions/yarp-ui/wiki/Configuration)
- [REST API](https://github.com/advanced-software-solutions/yarp-ui/wiki/REST-API)
- [Docker](https://github.com/advanced-software-solutions/yarp-ui/wiki/Docker)

## أوضاع الاستضافة

تأتي الواجهة على شكل مكتبة صفحات Razor (حزمة NuGet [**YA-RP-UI**](https://www.nuget.org/packages/YA-RP-UI)) ويمكن استضافتها بثلاث طرق:

**1. تنفيذي مستقل** — `YARPUI.Host` هو مضيف خفيف يشغّل البروكسي وواجهة الإدارة في تطبيق واحد:

```bash
cd YARPUI.Host && dotnet run      # → http://localhost:5080
```

**2. مضمّنة في تطبيقك** — أضف الحزمة واربطها (انظر `samples/EmbeddedHost`):

```xml
<PackageReference Include="YA-RP-UI" Version="0.2.1" />
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddYarpUi();               // إعدادات البروكسي والخدمات والمصادقة وصفحات Razor

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseYarpUiRequestLogging();     // يسجّل الطلبات المُمرَّرة لصفحة السجلات
app.MapYarpUi();                   // صفحات الواجهة + /api/yarp/*
app.MapReverseProxy();             // البروكسي نفسه (عام)
app.Run();
```

**3. مرفقة بتطبيق يهيّئ YARP بالفعل** — للبوابات التي لها `LoadFromConfig` أو مزوّدات مخصصة أو تحويلات وفلاتر خاصة. تعرض الواجهة كامل الإعدادات الحيّة للتطبيق **ويمكنها تعديلها**: عند الحفظ يُكتَب كل تغيير راجعًا إلى ملف `appsettings.json` الذي جاء منه المسار أو المجموعة، ويعيد YARP تحميل الملف تلقائيًا — تسري التعديلات دون إعادة تشغيل بينما يبقى كود التطبيق (التحويلات والوسائط وخط المعالجة المخصص) دون مساس:

```csharp
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(...);            // يبقى عملك المخصص مسيطرًا بالكامل

builder.AttachYarpUi();            // لا تسجيل للبروكسي ولا تهئة للإعدادات

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseYarpUiRequestLogging();
app.MapReverseProxy();
app.MapYarpUi();
```

سلوك التحرير بالكتابة الراجعة:

- **تُدمَج التعديلات في عُقد JSON الموجودة** — تحتفظ الحقول التي لا ينمذجها المحرّر (مثل `RateLimiterPolicy` أو المفاتيح المخصصة) بقيمها؛ ويُحافَظ على المحتوى غير ذي الصلة في الملف.
- **المسارات/المجموعات الجديدة** تُضاف إلى `appsettings.json`؛ و**المحذوفة** منها تُزال من كل ملفات appsettings التي تعرّفها (بما فيها ملفات تجاوز البيئة).
- **النسخ الاحتياطية**: أول مرة تعدّل فيها الواجهة ملفًا، تُحفَظ نسخة `.yarpui.bak` بجواره؛ وخيار *استعادة نسخة appsettings الاحتياطية* يعيد كل الملفات المعدَّلة إلى حالتها السابقة.
- العناصر الآتية من **مصدر ليس ملفًا** (مزوّد `IProxyConfigProvider` مخصص مدعوم بقاعدة بيانات أو كود ونحوه) تظهر مقفلة وللقراءة فقط — إذ لا يوجد ملف تُكتَب إليه التعديلات.
- الحالات الشاذة الموجودة مسبقًا في الإعدادات (مثل مسار يشير إلى مجموعة غير موجودة) لا تمنع الحفظ؛ وتُرفَض فقط المشاكل التي تُحدِثها التعديلات نفسها.

تقرأ جميع الأوضاع الإعدادات نفسها (بيانات اعتماد `YarpUi:Auth`) وتدعم `YarpUi:DataDirectory` لاستمرارية البيانات عبر وحدات التخزين. تصادق الواجهة بمخطط كوكيز خاص بها (`YarpUi.Auth`) ولا تغيّر أبدًا مخطط المصادقة الافتراضي للمضيف، لذا فهي آمنة بجانب إعداد JWT/كوكيز موجود في التطبيق.

## البدء السريع

```bash
dotnet run
```

افتح http://localhost:5080 وسجّل الدخول. بيانات الاعتماد الافتراضية (غيّرها!):

| الإعداد | القيمة |
| --- | --- |
| اسم المستخدم | `admin` |
| كلمة المرور | `yarp-admin` |

كلاهما مُهيّأ في `appsettings.json` تحت `YarpUi:Auth`.

## Docker

تُنشَر الصور الجاهزة على [Docker Hub (`amrfswalha/yarp-ui`)](https://hub.docker.com/r/amrfswalha/yarp-ui):

```bash
docker run -d -p 8090:8080 -v yarp-ui-data:/app/data amrfswalha/yarp-ui:latest
```

أو ابنِ من المصدر باستخدام قالب `docker-compose.yml` المرفق مع الحل:

```bash
docker compose up -d --build
```

تُقدَّم الواجهة بعدها على **http://localhost:8090**. تُحفَظ كل الإعدادات القابلة للتغيير في وحدة تخزين داخل `./docker-data` لتنجو من `docker compose down`:

| الملف | الغرض |
| --- | --- |
| `docker-data/appsettings.json` | بيانات الاعتماد (`YarpUi:Auth`) وإعدادات `ReverseProxy` الأولية — عدّلها على المضيف وتُطبَّق عند التشغيل التالي |
| `docker-data/yarp-ui.routes.json` | يُكتَب تلقائيًا عند كل حفظ من محرّر الواجهة |
| `docker-data/yarp-ui-logs.db` | قاعدة بيانات سجل الطلبات (SQLite) — تنجو من إعادة التشغيل وتُنقَّح وفق سياسة الاحتفاظ |

تحت الغلاف، يضبط الحاوية `YarpUi__DataDirectory=/app/data` ويوصل وحدة التخزين هناك؛ وملف `appsettings.json` في ذلك الدليل يغلب المدمج في الصورة (يعمل هذا دون Docker أيضًا — وجّه `YarpUi:DataDirectory` إلى أي مكان تريده). لبناء الصورة يدويًا: `docker build -t yarp-ui:0.2.1 .` من جذر الحل.

## IIS

تعمل الاستضافة تحت IIS بهوية مجموعة التطبيقات الافتراضية (`ApplicationPoolIdentity`)، التي تملك وصولًا **للقراءة فقط** إلى مجلد الموقع. عند التشغيل تكتشف YARP UI أن جذر المحتوى غير قابل للكتابة فتخزّن كل الحالة القابلة للتغيير — `yarp-ui-logs.db` و`yarp-ui.routes.json` وملف `appsettings.json` الاختياري لدليل البيانات — تحت `%ProgramData%\YarpUi\<اسم التطبيق>` بدلًا من ذلك، مع تسجيل تحذير لتكون عملية النقل مرئية. لا يحتاج الأمر أي تهيئة؛ يبدأ البروكسي بشكل طبيعي وتعمل صفحتا المحرّر والسجلات على مجلد الاحتياط هذا.

للاحتفاظ بالحالة في مكان تختاره بدلًا من ذلك، وجّه `YarpUi:DataDirectory` إلى مجلد قابل للكتابة، أو امنح هوية مجموعة التطبيقات صلاحية الكتابة على مجلد الموقع:

```powershell
icacls "<site folder>" /grant "IIS AppPool\<YourAppPool>:(OI)(CI)(M)"
```

لا يُستبدل `YarpUi:DataDirectory` المُهيّأ صراحةً بمسار الاحتياط أبدًا. ويمكن إعادة توجيه موقع الاحتياط نفسه عبر `YarpUi:FallbackDataDirectory`.

## كيف تعمل الإعدادات

```
appsettings.json ("ReverseProxy" section)   ← hand-written seed
                │
                ▼  startup
   yarp-ui.routes.json (if present)         ← takes precedence once it exists
                │
                ▼
   InMemoryConfigProvider (live YARP config)
```

- عند التشغيل يحمّل التطبيق `yarp-ui.routes.json` إن وُجد؛ وإلا فيقرأ قسم `ReverseProxy` من `appsettings.json`.
- أول عملية **حفظ** في المحرّر تكتب الإعدادات كاملة إلى `yarp-ui.routes.json`. من تلك اللحظة يصبح هذا الملف مصدر الحقيقة — ويُترَك `appsettings.json` دون تعديل.
- **العودة إلى appsettings.json** (المحرّر، أسفل اليسار) تحذف الملف المُدار من الواجهة وتعود إلى الإعدادات الأولية.
- تُتحقَق صحة عمليات الحفظ بمُتحقِّق الإعدادات الخاص بـ YARP نفسه؛ وتُرفَض الإعدادات غير الصالحة ويواصل البروكسي العمل بآخر إعدادات صالحة.

## سجلات الطلبات

لا تُسجَّل إلا الطلبات **المُمرَّرة عبر البروكسي** (تُستبعد طلبات الواجهة/API). تُخزَّن الإدخالات في قاعدة بيانات SQLite (`yarp-ui-logs.db` في دليل البيانات، بجوار `yarp-ui.routes.json`) وتنجو من إعادة التشغيل. يلتقط كل إدخال الطريقة ومسار URL ورمز الحالة والمدة والمسار/المجموعة/الوجهة التي اختارها YARP وعنوان IP للعميل. وتُنقَل قواعد البيانات التي أنشأتها إصدارات أقدم في مكانها عند أول تشغيل.

**عنوان IP للعميل** هو الإدخال الأول (الأيسر) في `X-Forwarded-For` إذا وفّره بروكسي أمامي، وإلا فعنوان الاتصال المباشر. لا تثبّت الواجهة وسيط ForwardedHeaders بنفسها — فإذا كان التطبيق كله خلف موزّع أحمال فإن الترويسة تعكس ما مرّره ذلك البروكسي. وبما أن `X-Forwarded-For` قابل للتلاعب من جهة المُرسِل، تعامل مع عناوين IP المسجَّلة كمعلومات استرشادية لا كمعلومات مُصادَق عليها.

تعرض صفحة السجلات الإدخالات **الأحدث أولًا** وكل عمود فيها قابل للفرز. تعمل مرشّحات المسار/المجموعة/الوجهة ومحدد الإطار الزمني (آخر 15 دقيقة … 7 أيام، أو نطاق مخصص، أو كل الوقت) على إجراء **بحث من جانب الخادم عبر كامل السجل المحفوظ** عبر `GET /api/yarp/logs` مع `from`/`to` (بالملي ثانية بصيغة Unix) و`routeId` و`clusterId` و`destinationId` و`sort` و`desc` و`limit` (1000 كحد أقصى لكل استعلام). ومن دون معاملات بحث يحافظ نقطة النهاية على سلوك التتبّع المباشر: يبثّ `after=<seq>` الإدخالات الجديدة من الأقدم إلى الأحدث. وتُطبَّق التصفية النصية الحرة وتصفية فئات الحالة فوق ما تم تحميله.

**سياسة الاحتفاظ** تحذف السجلات تلقائيًا متى تجاوزت عمرًا معينًا: تعمل مهمة في الخلفية عند التشغيل ثم كل ساعة. تُدار السياسة من شريط أدوات صفحة السجلات (*الاحتفاظ بالسجلات: للأبد / 1 / 7 / 30 / 90 / 365 يومًا*) ويُطبَّق التغيير فورًا؛ والقيمة الافتراضية الأولية تأتي من `YarpUi:Logs:RetentionDays` في الإعدادات (30 يومًا إن لم تُضبَط). وتُخزَّن السياسة التي تضعها في الواجهة في قاعدة البيانات نفسها وتغلب على قيمة الإعدادات.

## دون اتصال / بلا شبكة

جميع مكتبات JavaScript (Cytoscape.js وdagre وcytoscape-dagre وChart.js) مضمّنة محليًا تحت `wwwroot/lib/`. لا يُستخدَم أي CDN وقت التشغيل؛ وتعمل الواجهة دون اتصال بالكامل.

## ملاحظات أمنية

- تتطلب واجهة الإدارة تسجيل الدخول (مصادقة بالكوكيز). **بينما تكون مسارات البروكسي نفسها عامة** — فهذا هو الغرض من البروكسي أصلًا.
- توجد بيانات الاعتماد كنص صريح في `appsettings.json`، وهذا مقبول لأداة محلية/داخلية. إذا عرضت هذا التطبيق لما هو أبعد من localhost، فضعه خلف HTTPS واستخدم بيانات اعتماد قوية، وفكّر في توسيع المصادقة بكلمات مرور مُعمّاة (hashed) أو مزوّد هويات حقيقي.
- استخدم HTTP على شبكة موثوقة فقط؛ فالكوكي غير مُعلَّم `Secure` كي يعمل أيضًا على HTTP العادي أثناء التطوير.

## الترخيص

حقوق النشر 2026 لمؤلفي YARP UI.

مرخّص بموجب [رخصة Apache، الإصدار 2.0](LICENSE). هذا هو الإصدار المجتمعي من YARP UI؛ ويُرخَّص الإصدار premium على حدة ويُوزَّع من مستودعه الخاص.

«YARP UI» وشعار YARP UI علامتان تجاريتان للمشروع؛ ولا تمنح هذه الرخصة أي حقوق لاستعمالهما في تسويق منتجات مشتقة.

المكتبات الخارجية المضمّنة (YARP وMicrosoft.Data.Sqlite وCytoscape.js وdagre وcytoscape-dagre وChart.js) مرخّصة بترخيص MIT — انظر [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
