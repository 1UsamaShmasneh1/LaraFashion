# قبول Linux المحلي عبر Docker Desktop — 10 أكتوبر 2026

النتيجة: **PASS** للجولة الكاملة على آخر نسخة من أدوات الاختبار. نجحت **91/91** مجموعة .NET و**42/42** حالة Bash وجميع فحوص Linux الإلزامية، دون FAIL أو SKIP. انتهت الحاوية برمز `0` ودون OOM، بعد تنفيذ مهلة القفل الفعلية `900` ثانية. حُفظت الأدلة قبل تنظيف موارد المهمة.

بدأت حاوية الجولة النهائية في `2026-10-10T16:50:08.930359879Z` وانتهت في `2026-10-10T17:05:26.433163802Z`. تحقق فحص البصمات النهائي داخل Linux وعلى المضيف من ثبات جميع مدخلات المصدر أثناء الجولة.

هذه جولة قبول محلية لشجرة العمل الحالية. لا تستعمل بيانات الإنتاج ولا تتصل بـOracle أو SSH أو تنفذ GitHub Actions. لم يحدث commit أو push أو تغيير فرع أو reset.

## المصدر المختبر

- الفرع: `main`.
- HEAD المرجعي: `b196d7268e2b5a5b73b207d963255d5d6a7c0129`؛ الشجرة معدلة وتتضمن ملفات غير متتبعة، ولذلك لا يمثل HEAD وحده الكود المختبر.
- RunId النهائي: `lf-linux-20261010-7f78c9bd-round7`.
- بصمة المصدر المختبر: `C129109E1D6F6CDFAA3BBA705BA63DA741C5F7BE669A8D78C8452A08556D0FFF`، وهي SHA256 للملف المولد `source-fingerprint.json` الذي يحتوي HEAD وبصمات المدخلات.
- بصمة manifest الجولة: `106B009005D301AA9635A64E02059E99FEFA7DD6A692598B136DE0F07FEFE5AA`.
- خط الأساس الخاص بالمهمة: 165 ملفًا؛ بصمة سجله `DAEDFAE9CC1F55AB0EB72626B50AB7EFB75D368B46C50F4EC53F6D0C7410FB73`.

نسخ الـlauncher 158 ملف مصدر حاليًا بالقائمة المسموحة، وأضاف `NuGet.Config` عامًا مولدًا: 159 مدخلًا. قارن SHA256 قبل النسخ وبعده، وتتحقق الجولة من المصدر داخل Linux في البداية والنهاية، ثم يتحقق الـlauncher من الملفات الأصلية بعد التنفيذ. نُقلت البايتات كما هي؛ رُفض CR في ملفات `.sh`، ولم يحدث تحويل صامت للأسطر. يبدأ التنفيذ عبر `bash` و`python3` دون الاعتماد على executable bit في Windows.

مجلد الإدخال خارج المستودع تحت `%TEMP%\<RunId>\input`. لا يوجد ربط للمستودع أو مجلدات المضيف بالحاوية. استُبعدت قواعد البيانات وWAL/SHM والنسخ الاحتياطية والمفاتيح والشهادات و`.env` وappsettings الفعلية ومفاتيح Data Protection و`.git/.vs/bin/obj` وuploads والصور وأدلة الإنتاج التفصيلية. الإعدادات وقواعد الاختبار والحسابات والمفاتيح كلها اصطناعية وتُنشأ داخل الحاوية.

## البيئة المسجلة

| العنصر | القيمة الفعلية |
| --- | --- |
| Docker context / endpoint | `desktop-linux` / `npipe:////./pipe/dockerDesktopLinuxEngine` |
| عميل / محرك Docker | `29.7.2` / `29.7.2` |
| Docker Desktop | `4.86.0 (236216)` |
| الصورة الرسمية | `mcr.microsoft.com/dotnet/sdk:9.0.314-noble`، `linux/amd64` |
| digest الأساس | `sha256:fda2105a0319b5ae5d7d51bd540ab0f7fc548ee6ea48cbc95c2b63b62ecad3a3` |
| صورة الجولة المحلية | `sha256:09c2fa87740a13e1cddd3badce8cb20397cca3275fb7248d1138ac8d0d3a3790` |
| نظام الصورة | Ubuntu 24.04، Noble |
| kernel / architecture | `6.18.33.2-microsoft-standard-WSL2` / `x86_64` |
| .NET SDK | `9.0.314` |
| runtimes | `Microsoft.NETCore.App 9.0.16` و`Microsoft.AspNetCore.App 9.0.16` |
| EF / dotnet-ef | `9.0.16`، وفق الاعتماديات وtool manifest الحاليين |
| SQLite التطبيق | `3.46.1` من `libe_sqlite3.so` المنشورة داخل Linux |
| SQLite Python | `3.45.1` |
| Python / Bash / flock | `3.12.3` / `5.2.21` / util-linux `2.39.3` |
| fuser / procps / iproute2 / strace | psmisc `23.7-1build1` / `2:4.0.4-4ubuntu3.3` / `6.1.0-1ubuntu6.4` / `6.8-0ubuntu2` |
| curl / git / coreutils | `8.5.0` / `2.43.0` / `9.4` |

الصورة المحلية القديمة وفرت SDK/runtime 10 فقط؛ لم تُستعمل لتشغيل net9.0. وافق المستخدم صراحة على تنزيل صورة Microsoft المحددة وأدوات Ubuntu الرسمية واستعادة الاعتماديات الحالية وdotnet-ef دون تحديثها. لم تُثبت أدوات على Windows أو داخل docker-desktop يدويًا، ولم يتغير TargetFramework أو أي PackageReference أو إعداد roll-forward.

كان المحرك المحلي يوفر 16 CPU ونحو 9.6 GiB RAM ومساحة قرص كافية. حاوية الجولة محدودة بـ4 CPU و6 GiB و1024 PID، وmanaged GC heap بـ512 MiB لكل عملية. تُنفذ حالات Bash بعامل واحد لضبط الموارد؛ حالة التنافس نفسها تستخدم عمليتين مستقلتين. سجل الحزم الكامل محفوظ ضمن الأدلة الآمنة.

تجهيز الصورة والاستعادة وحدهما استخدما الشبكة. التنفيذ `--network none`، دون نشر منافذ أو host networking أو Docker socket أو privileged أو mounts. HTTP على `127.0.0.1:59993` داخل الحاوية. ملفات DB والقفل والحالة والنسخ المؤقتة وTEMP ومفاتيح الاختبار على Linux `overlayfs`، دون Windows bind أو tmpfs. Ubuntu/kernel Docker Desktop لا يمثلان نظام Oracle أو kernel الخادم.

## نتائج الجولة النهائية

| المسار | النتيجة الفعلية |
| --- | --- |
| Linux .NET tests | PASS لجميع مجموعات الـrunner الحالي: 91/91؛ تضم الـ88 السابقة والتوسعات المحلية السابقة لهذه المهمة |
| Full Bash harness | PASS لـ42 سيناريو مميزًا على النسخة النهائية: 34 حالة مشتركة + حالتا Linux للقفل + 3 للصلاحيات + 3 للإشارات |
| FD9/flock الحقيقي | PASS: توريث وصف القفل، رفض flag بلا قفل والوصف المغلق والخاطئ وغير المقفول، تعارض فتح مستقل، تسلسل عمليتين، بقاء القفل مع الابن بعد موت الأب، تحريره بعد موت الابن، مهلة الإنتاج الفعلية 900 ثانية |
| File identity/state | PASS: هوية التطبيق عبر fstat/device/inode؛ symlink وhardlink يتطابقان، ونسخة مطابقة المحتوى تختلف؛ fsync ملف ثم rename ثم fsync مجلد؛ قارئ مستقل أثناء الاستبدال، وقراءة جديدة بعد SIGKILL |
| Unix permissions | PASS: UID الخدمة `1654` يقرأ ويعبر؛ منع القراءة أو العبور يرفض المرشح قبل stop؛ UID `65534` لا يقرأ المفاتيح/الطلب/DB/الأدلة الاصطناعية الخاصة |
| Failure/recovery | PASS: جميع حالات Bash الحالية، INT/TERM/HUP بأكواد 130/143/129، SIGKILL ثم رفض الاستئناف التلقائي، حفظ الكتابة الجديدة، رفض restore بعد محاولة التشغيل، وفحص CLI مستقل لـ`RecoveryStartAttempted` دون تغيير DB/WAL |
| Real Linux application HTTP | PASS: التطبيق المنشور الحقيقي في Production، GET 200 JSON بلا redirect، commit/schema/هوية DB/PID، مع إثبات مالك المنفذ بواسطة fuser وss الحقيقيين وUID العملية |
| Synthetic Data Protection restart | PASS: ثلاث عمليات للتطبيق: preview ثم confirm ثم replay؛ نفس المفاتيح والمجلد وApplicationDiscriminator الافتراضي، كمية 3 ثم 5 ثم 5، وحركات 0 ثم 1 ثم 1 |
| Server systemd/configuration readiness | لم يُختبر هنا؛ يتطلب مراجعة مستقلة على الخادم بموافقة صريحة |

تُفصل مقارنة بيانات الأعمال قبل startup عن المقارنة بعد GET. يسجل اختبار الـhost مقارنة بعد انتهاء Seeder وقبل عملية المراجعة؛ وتُقارن DB مرة أخرى بعد HTTP. إنشاء مفتاح DP وحفظ طلب المراجعة متوقعان داخل fixture الخاصة ولا يُحسبان كتابة أعمال من GET. استُعمل `StartupHook` اختباري لمراقبة خدمات الـhost الفعلي؛ لا يضيف endpoint ولا يبدل تسجيلات التطبيق. في replay تُفك التذكرة أيضًا بالـprovider الحقيقي لإثبات تحميل مفاتيح الجولة السابقة. لم يتغير `SetApplicationName` أو عمر التذكرة أو idempotency.

يختبر التطبيق الحقيقي كذلك رفض startup عند DB مفقودة أو مخطط غير متوافق أو إعداد JWT لازم مفقود أو DP مفقود أو DP داخل الإصدار؛ يتحقق من عدم إنشاء DB مفقودة وعدم تغير DB السليمة.

systemctl والخدمة وcurl/git/build/publish/fuser الخاصة بسيناريوهات Bash تبقى محاكاة، مع أدوات الصيانة وSQLite الحقيقيين ومخرجات Linux المنشورة. نشر Linux الفعلي وHTTP وfuser/ss منفصلان عن هذه المحاكاة. صلاحيات حالات Linux تستخدم runuser وtest الحقيقيين. لا يوجد mock لـflock أو fstat أو fsync في فحوص نجاحها. حالة fsync السلبية تحقن EIO بواسطة strace لاختبار انتشار الخطأ فقط؛ وحالة رفض فتح المجلد تحت UID الخدمة فشل Unix حقيقي. قتل العملية لا يثبت انقطاع طاقة أو متانة قرص Oracle. اختبارات Git المحلية الـ15 من التقرير السابق تبقى دليلًا منفصلًا؛ لم يُنقل تاريخ `.git` لهذه الجولة ولا تُنسب تلك النتائج إلى Linux هنا.

## العيوب التي ظهرت وإصلاح أدوات الاختبار

الجولات 1–6 تشخيصية فاشلة ولا تُجمع مع نتيجة الجولة النهائية:

1. صلاحية tool-drop منعت UID الخدمة من بلوغ فحص fsync السلبي: أصبحت حزم التنفيذ الاصطناعية مقروءة، وبقيت fixtures والأدلة والمفاتيح خاصة.
2. TEMP الخاص بالـroot منع Data Protection من إنشاء ملفه المؤقت تحت UID الخدمة: أُنشئ service TEMP خاص `0700` مملوك للـUID نفسه وخارج الإصدار. فُصلت نسخة HTTP ذات manifest عن app-drop المصدر لحالات publish في Bash.
3. Docker الافتراضي يمنع root من فحص FD لعملية UID آخر: يُنفذ fuser/ss الحقيقيان تحت UID العملية، دون إضافة capabilities.
4. الجولتان 4 و6 خرجتا بـ`137 / OOMKilled`: أثبت اختبار قصير أن اسم `dotnet` كان يعيد الدخول إلى mock نفسه عبر PATH. حُسم المسار الحقيقي قبل إدخال mocks، ويُرفض الاسم النسبي أو executable داخل مجلد mocks فورًا. أُضيف regression ينفذ الرفض دون استدعاء البديل. حدود الذاكرة والتسلسل بقيت ضبطًا لموارد الاختبار.
5. سجل تجهيز fixture سبق JSON في stdout: أصبحت metadata الاختبار ملفًا خاصًا بدل تحليل stdout المختلط. أُضيف فحص CLI مستقل لمنع restore بعد `RecoveryStartAttempted`.

يستخدم اختبار التنافس FIFO لإطلاق المرحلة المحمية بعد إثبات قفل مستقل، ويتحقق أن اكتمال الأولى يسبق دخول الثانية؛ لا يعتمد على sleep وحده. جميع السيناريوهات الحالية والإضافات تُعاد على آخر نسخة، دون حذف أو skip أو تخفيف validator.

## الملفات والأدلة وإعادة التشغيل

ملفا الاختبار الموجودان المعدلان: `LaraFashion.IntegrationTests/Program.cs` لإضافة مدخل الاختبار الخاص فقط، و`deploy/tests/harness.py` للفحوص الحقيقية والتزامن وحسم dotnet. الملفات الجديدة: `LaraFashion.IntegrationTests/LinuxAcceptance.cs` و`deploy/tests/linux-acceptance.py` و`deploy/tests/linux-docker.ps1`. لا تغييرات تطبيق/مخطط/ترحيلات/UI/إعدادات اتصال/سكربت نشر/Actions لهذه المهمة.

بقيت 163 من ملفات خط الأساس الـ165 كما هي، ومن بينها كل ملفات التطبيق والترحيلات وModelSnapshot وRazor/CSS والمشاريع والأدوات وActions وسكربت النشر والتقارير السابقة المسجلة. تحتوي أدلة المدخلات البصمات الأصلية والنهائية لكل ملف؛ الاستثناءان هما ملفا الاختبار المذكوران فقط.

نجح آخر build Windows بـ0 أخطاء و0 تحذيرات، وbuild Linux الجديد بـ0 أخطاء و4 تحذيرات CS0618 سابقة عن SkiaSharp في `LaraFashion/Program.cs` و`ImageMaintenanceService.cs`. فحص EF أكد عدم وجود تغييرات في النموذج منذ آخر migration. الجولة تبني وتنشر من المصدر داخل Linux ولا تنقل bin/obj من Windows. أوامرها الأساسية:

```text
dotnet tool restore --configfile NuGet.Config
dotnet restore LaraFashion.IntegrationTests/LaraFashion.IntegrationTests.csproj --configfile NuGet.Config
dotnet build LaraFashion.IntegrationTests/LaraFashion.IntegrationTests.csproj -c Release --no-restore -p:UseSharedCompilation=false -p:SourceRevisionId=<HEAD>
dotnet tool run dotnet-ef migrations has-pending-model-changes --project LaraFashion --configuration Release --no-build
dotnet publish LaraFashion -c Release --no-build --no-restore -o <Linux app-drop>
dotnet publish LaraFashion.IntegrationTests -c Release --no-build --no-restore -o <Linux tool-drop>
dotnet <Linux tool-drop>/LaraFashion.IntegrationTests.dll
bash deploy/tests/run-harness.sh
```

لإعادة الجولة من PowerShell:

```powershell
& C:\dev\LaraFashion\deploy\tests\linux-docker.ps1 -AllowDownloads
```

يتطلب الـlauncher سياق Docker Desktop Linux المحلي ويتركه كما هو، ويطلب إذن التنزيل بالـflag قبل تجهيز صورة جديدة. اسم RunId جديد تلقائيًا. `-PreparedImage` يقبل صورة محلية مطابقة تمامًا لبصمة المصدر؛ `-KeepResources` اختياري للتشخيص. افتراضيًا تُستخرج الأدلة إلى `%TEMP%\<RunId>` ثم تزال حاوية وصورة الاختبار المملوكتان للـlauncher. ملف Dockerfile مولد في حزمة الإدخال المؤقتة فقط ولا يصبح مسار نشر.

حُفظت قائمة أسماء الحالات الـ91 والـ42 ونتائج الفحوص الإضافية وسجل البيئة والبناء في [LinuxDockerAcceptanceResults.log](LinuxDockerAcceptanceResults.log)، وmanifest/binaries ومقارنة الملفات المحمية والجرد والتنظيف في [LinuxDockerAcceptanceInputs.json](LinuxDockerAcceptanceInputs.json). لا تُجمع تسميات الفحوص الإضافية مع عدد الحالات لأنها تتداخل معها. عدد SKIP الإلزامي صفر. لا تتضمن الأدلة مفاتيح Data Protection أو DB أو طلب المراجعة أو snapshot إنتاج.

## موارد المهمة وتنظيفها

| الموارد المحلية المملوكة للمهمة | المصير بعد حفظ الأدلة |
| --- | --- |
| `lf-linux-20261010-7f78c9bd-probe` | حُذفت الحاوية؛ الصورة القديمة SDK 10 محفوظة |
| `lf-linux-20261010-7f78c9bd-recursion-proof` | حُذفت حاوية إعادة إنتاج خلل PATH |
| `lf-linux-20261010-7f78c9bd-round1-test` إلى `round7-test` | حُذفت الحاويات السبع بعد توقفها؛ نتائج الجولات الفاشلة محفوظة للتشخيص |
| `larafashion-linux-acceptance:lf-linux-20261010-7f78c9bd-round1` إلى `round7` | حُذفت صور الاختبار السبع بعد مطابقة IDs وlabels |
| صورة `mcr.microsoft.com/dotnet/sdk:9.0.314-noble` الجديدة لهذه المهمة | حُذفت بعد التحقق من digest وعدم استخدامها أو مشاركتها |
| volumes / networks خاصة | لم تُنشأ |

المجموع المحذوف: 9 حاويات و7 صور مشتقة وصورة اعتماد SDK 9 واحدة. لا تبقى حاوية أو صورة مسماة لهذه المهمة. بقيت الحاويات الخمس المتوقفة لمشروع آخر و`alssareea-postgres` العاملة كما كانت، وكذلك الصور السابقة. لم يُنفذ prune عام؛ بقي BuildKit cache الذي يحتوي مدخلات المصدر والاعتماديات المراجعة فقط. بيانات fixtures ومفاتيح DP كانت في الطبقات القابلة للكتابة للحاويات المحذوفة ولم تُصدّر.

السجلات الآمنة التفصيلية وحزم المصدر محفوظة محليًا تحت `%TEMP%\lf-linux-20261010-7f78c9bd-round7`؛ الأدلة المنشورة داخل المستودع هي الملفات الثلاثة المذكورة أعلاه. بصمات الصور والحاويات وحالات خروج الجولات التشخيصية ومصير الموارد مدونة في سجل المدخلات.

لا يلزم نشر أو migration لهذه الإضافات الاختبارية. جاهزية EnvironmentFile وDP الحقيقي وكتّاب الخدمة وتوافق الإصدار السابق على الخادم تبقى مرحلة منفصلة؛ نجاح Docker لا يعني أن الإنتاج جاهز.
