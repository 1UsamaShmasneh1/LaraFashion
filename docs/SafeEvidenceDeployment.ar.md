# دمج إثبات الأهلية في النشر الآمن — 6 أكتوبر 2026

متابعة 10 أكتوبر بعد استعادة Oracle: [تأمين النشر ومتطلبات قبول الخادم الجديد](RecoveryDeploymentSecurity.ar.md). التقرير الحالي يحتفظ بنتائج 6 أكتوبر؛ المتابعة توثق حواجز Git/SSH والنقل والاستعادة الجديدة ونتائجها المستقلة.

اكتمل التكامل المحلي للأدوات ومسار النشر. قبول تنفيذ Linux غير مكتمل: الاختبارات الوظيفية المتاحة نُفذت بـGit Bash على Windows، مع محاكاة الخدمة والشبكة والقفل. لا يُعد هذا اعتمادًا لنشر إنتاجي. لم تُنفذ قرارات على الطلبات المرجعية.

## A. خط الأساس المحفوظ

الفرع `main` وHEAD هو `b196d7268e2b5a5b73b207d963255d5d6a7c0129`، وبقيا كما هما. إصلاحات هوية المقاسات والمراجعة السابقة محلية وغير مرفوعة؛ النسخة المختبرة هي شجرة العمل المعدلة، وليست HEAD وحده. أُعيد تشغيل baseline فعليًا وكانت النتيجة **68/68** قبل التكامل.

سُجل status وبصمات مدخلات baseline في `TEMP/LaraFashion-deploy-integration-baseline`. بصمة ملف `inputs.json` الأصلي:

```text
27808859BC6868568B91DE24973F0307033F32646D6147635C33BA9CA1B16C9A
```

المقارنة النهائية مع baseline تحفظ Migrations الثلاث وحراسها وModelSnapshot وخدمات وقواعد المخزون والسلة والخصومات والصور والتصنيفات وكل Razor/CSS السابق. لم تُعدل أي Migration أو تُضاف Migration جديدة. بقي ملف المستخدم `New Text Document.txt` ببصمته `E0EC03301A24923122D48B695B14D247D0589600FD9BA54CE01B3CBCD904950F`.

ملف [مدخلات الاختبار](SafeEvidenceDeploymentInputs.json) يسجل 153 مدخلًا وإصداري DLL التطبيق والأداة المنشورين؛ بصمة الملف `00CFA2C1D5CA725786EE45D8DA1732EC8FD883689E8AE1B6E67CB976D96875E0`. من ملفات baseline الـ150 تغيرت 11 فقط ضمن هذه المتابعة، تشمل ثلاث إحالات توثيق؛ بقيت البقية ببصماتها. سجلات النتائج المنشورة هنا تحتوي اختبارات اصطناعية وملخصات فقط؛ قواعد SQLite وخطط الإثبات التفصيلية ومعلومات الزبائن بقيت خارج Git.

## B. الملفات والدوال المتغيرة في هذه المتابعة

| الملف | التغيير |
|---|---|
| `deploy/deploy-production.sh` | مراحل النشر، بناء Release قبل التوقف، قفل FD9، منع الكتّاب، النسخ والتجربة والإثبات، الصحة والتعافي المحدود |
| `.github/workflows/deploy.yml` | بوابات تحقق مستقلة قبل SSH، SHA واحد، مهلات وconcurrency السابقة؛ الصيغة JSON صالحة ضمن YAML 1.2 |
| `LaraFashion.IntegrationTests/DeploymentDatabase.cs` | `Backup`, `Classify`, `Plan`, `Prepare`, `Apply`, `Verify`, `Restore`, `Compare`, حفظ الحالة |
| `LaraFashion.IntegrationTests/DeploymentMaintenance.cs` | dispatcher صارم وعقد نتائج v1 وفحص النتائج والحالة وإعداد الخدمة وحزمة الإصدار وتوافق التعافي |
| `LaraFashion.IntegrationTests/LegacyReviewEvidence.cs` | رفض المصدر والهدف إذا كانا الملف الفعلي نفسه، بما فيه hardlink |
| `LaraFashion.IntegrationTests/ReviewUpgradeVerification.cs` | استعمال محرك المقارنة والتجربة المشترك بدل نسخة تحقق منفصلة |
| `LaraFashion.IntegrationTests/Program.cs` | توجيه أوامر الصيانة، رفض arguments غير المعروفة دون fixtures، تسجيل اختبارات النشر |
| `LaraFashion.IntegrationTests/DeploymentSafetyTests.cs` | 20 مجموعة جديدة فوق المجموعات الـ68 |
| `LaraFashion.IntegrationTests/DeploymentHarness.cs` | مساعد CLI اصطناعي، مقيد بمجلد TEMP وعلامة harness، وحقن فشل Migration حقيقي |
| `deploy/tests/harness.py`, `run-harness.sh` | تنفيذ Bash الحقيقي مع SQLite والأداة المنشورة، ومحاكاة الخدمات والشبكة والبناء المتكرر |
| `LaraFashion/Data/DeploymentReadiness.cs` | فحص بدء التشغيل والصحة للقراءة فقط، مع هوية الملف الفعلية وتعريفات الحراس |
| `LaraFashion/Program.cs` | منع ترحيل startup التلقائي، رفض DB مفقودة، هوية/بصمة الإصدار، DP دائم، عقد GET health دون redirect |
| `LaraFashion/Services/AppStoragePaths.cs` | مسارات الإنتاج قابلة للضبط ببيئة الخدمة، مع الحفاظ على القيم المرجعية |
| `LaraFashion/LaraFashion.csproj` | استبعاد DB وsidecars وuploads والمفاتيح وappsettings من publish، دون تغيير الحزم |
| ملفات هذا التقرير وسجلاته وإحالات التقارير السابقة | توثيق الحدود والنتائج وخطوات التشغيل المستقبلية |

لم يتغير تصميم الواجهة أو النص التجاري أو خوارزمية أهلية التسوية أو قرارات المدير. لم تنشأ طبقة أدوات أو مشروع جديد.

## C. المراحل وحالات القاعدة

```text
Prepared → WritersStopAttempted → WritersStopped → BackupVerified
→ PlanVerified → TargetFrozen → RehearsalVerified
→ EvidencePrepareAttempted → EvidenceVerified
→ SchemaChangeAttempted → SchemaVerified
→ FilesSwapAttempted → FilesSwapped → ApplicationStartAttempted
→ HealthVerified → Completed
```

كل نية توقف/تعديل/تبديل/تشغيل تُسجل قبل تنفيذها. لا تُقفز المراحل. الحالة والخطة والنسخة والأداة والسجلات تحت `backups/deploy-attempts/<attempt>` بصلاحيات مقيدة، خارج الإصدار والويب. على Linux يُنفذ `fsync` للملف وللمجلد بعد الاستبدال الذري للحالة.

| الحالة | الخطة |
|---|---|
| `BeforeIdentity` | أحدث نسخة SQLite بعد توقف الكتّاب هي مصدر الإثبات السابق للهوية؛ المجموعة تُحسب من جميع أسطرها ديناميكيًا |
| `IdentityOnly` | مصدر صريح موثوق سابق للهوية وبصمة SHA256؛ أهلية المطابق فقط، مع أعداد تغير/اختفاء/هوية موجودة/NULL غير موجودة في المصدر |
| `ReviewApplied` | لا إعادة استيراد أو فتح جدول مختوم؛ مقارنة السجل والتسويات السابقة، بما فيه إثبات أسطر حُذفت عند تنظيف الطلبات |
| جزئي/غير معروف/أحدث | توقف؛ لا Down أو تغيير يدوي في history أو تشغيل تطبيق أقدم |

التصنيف يتحقق من تسلسل history والأعمدة وأنواعها وقيودها الأساسية وFKs وindexes وتعريفات triggers والمقاسات، بمقارنة مخطط مرجعي مولد من Migrations المرشح. غياب إثبات مطلوب خلف الإغلاق ينتج `ClosedEligibilityNeedsSeparateRepair`؛ إعادة النشر لا تعالجه. صفر أسطر مقبول بعد تحقق كامل.

## D. إثبات اكتمال الأهلية

`BackupDatabase` في Microsoft.Data.Sqlite ينشئ نسخة متسقة مستقلة، تشمل البيانات الملتزمة في WAL. لا fallback إلى `cp`، ولا تغيير journal للمصدر، ولا حذف WAL/SHM يدويًا. يُفحص integrity/FKs ومقارنة الأعمال وتغير المصدر. هويات الملفات تمنع alias عبر الروابط، وبصمة المصدر تُراجع أثناء الاستخدام وبعده.

المجموعة المطلوبة تشمل `OrderItemId`, `OrderId`, `ProductId`, `ItemFingerprint`, `SourceSha256`, `SourceMigration`. تُقارن المجموعة الكاملة قبل ترحيل الهوية وبعده وقبل ختم المراجعة وبعد الاستيراد؛ العدد وحده لا يكفي. يفشل نقص/زيادة/استبدال معرفات مع العدد نفسه أو تبديل fingerprint أو ربط المصدر. تجهيز موجود مسبقًا لا يُقبل إلا إذا طابق المجموعة بالضبط.

مسار `--prepare-legacy-review --plan` يستدعي أداة التجهيز الأصلية بترتيب **الهدف ثم المصدر ثم البصمة**. `Apply` يطبق Identity ثم Review دون تشغيل التطبيق بينهما. يُحذف staging ويثبت الإغلاق وتعريفات الحراس؛ تبقى هوية السطر التاريخي NULL ولا تنشأ مراجعة أو حركة مخزون بواسطة Migration.

`Capture/Compare` يستخرجان كل الجداول الأصلية وكل أعمدتها وصفوفها، بما فيه جدول أعمال إضافي غير معروف مسبقًا. تُستثنى history والتجهيز المؤقت فقط في المواضع اللازمة؛ الجداول التقنية الجديدة المسموح بها معلنة بالاسم. التسويات الموجودة قبل إعادة النشر تُقارن كاملة، ولا يُطلب أن تكون صفرًا.

كل أمر ينتج JSON بعقد إصدار 1 وأعلام منفصلة. السكربت يشترط نجاح العملية **ونجاح فاحص نتيجة الآلة**. حقول مفقودة/JSON مبتور/إصدار غير مدعوم/خروج صفر مع أعلام ناقصة أو غير ناجحة توقف المسار. `RehearsalVerified` منفصل عن `BusinessPreserved`, `IntegrityVerified`, `GuardsVerified`, `EvidenceVerified`, `HealthVerified`.

الأسطر ذات المنتج المفقود والطلبات المغلقة وSandbox لا تُستبعد من الإثبات لمجرد هذه الصفات. الإثبات لا يعوض وجود منتج أو مقصد حالي ولا يجيز حركة مخزون محظورة وقت الاستخدام.

## E. التعافي وإعادة المحاولة والانقطاع

قبل التوقف والتغيير الحي، يبقى التطبيق والقاعدة كما هما. لا تُحذف إلا مؤقتات معروفة، والسياسة الحالية تحتفظ بأدلة المحاولة بدل pruning تلقائي.

بعد التوقف وقبل **أي محاولة تشغيل**، الاستعادة الآلية مشروطة بمصدر المحاولة الثابت، هوية الهدف، مقارنة جميع الأعمال، غياب أي تسوية جديدة، توقف الخدمات المعروفة وعدم وجود مقابض DB، وملف إصدار سابق يربط مخطط النسخة ببصمة DLL السابقة. تتم الاستعادة بآلية SQLite ثم integrity ومقارنة المخطط والأعمال. غياب ملف توافق موثوق للإصدار السابق يجعل النتيجة توقفًا يدويًا؛ لا يُخمن توافق الإصدار الأول السابق لهذا المسار.

بعد `ApplicationStartAttempted` أو `RecoveryStartAttempted`، تُمنع استعادة نسخة سابقة حتى عند فشل start أو عدم ظهور تسويات. فشل الصحة مع طلب جديد أو كتابة أخرى يحتفظ بالقاعدة، ويحاول إيقاف الكتّاب، ويسجل `ManualRecoveryRequired`. فشل stop/restore/start لا يُخفى ولا يحوّل النشر إلى نجاح. نجاح التعافي السابق يسجل `Recovered` مع بقاء exit النشر غير صفر.

عند وجود حالة سابقة غير مغلقة، تفحص المحاولة الجديدة الحالة الفعلية والمخطط للقراءة ثم تتوقف؛ لا استئناف أو استيراد تلقائي. تُعالج ERR/INT/TERM/HUP. SIGKILL وانقطاع الطاقة يتطلبان هذا الفحص اللاحق؛ لا يدعي trap معالجتهما.

الـwrapper يملك FD9، والسكربت يتحقق من مساره وتعارض open مستقل وملكية الوصف الموروث. لا يكفي `LARAFASHION_DEPLOY_LOCK_HELD=1` ولا يُكتسب قفل متداخل ينتظر نفسه. لا يُحذف ملف القفل. بقي `larafashion-production` و`cancel-in-progress=false`.

## F. الاختبارات الفعلية والبيئة

بيئة التنفيذ: Windows، SDK المثبت أصلًا `10.0.401`، الهدف `net9.0`، EF وأداة dotnet-ef الموجودان `9.0.16`. لم تُثبت أو تُحدث حزم أو أدوات. Git Bash هو MINGW/MSYS؛ WSL المتاح `docker-desktop` لا يحوي Bash/Python/.NET قابلة لتشغيل الاختبار، ولم تُثبت توزيعة أو يُعدل الجهاز.

```powershell
dotnet build LaraFashion.IntegrationTests -c Release --no-restore
$env:LARAFASHION_EF_CONNECTION='Data Source=<private-temp>/design-only.db;Mode=ReadWrite'
dotnet tool run dotnet-ef migrations has-pending-model-changes --project LaraFashion --configuration Release --no-build
dotnet run --project LaraFashion.IntegrationTests -c Release --no-build
dotnet publish LaraFashion -c Release --no-build --no-restore -o '<private-temp>/app-drop'
dotnet publish LaraFashion.IntegrationTests -c Release --no-build --no-restore -o '<private-temp>/tool-drop'
```

نتيجة baseline **68/68**، والنتيجة النهائية **88/88** في [السجل](SafeEvidenceDeploymentTestResults.log). البناء نجح؛ تبقى تحذيرات SkiaSharp الأربعة السابقة عند compilation كاملة، والبناء incremental الأخير أظهر صفر تحذيرات. فحص model لا تغييرات، و`git diff --check` بلا أخطاء. نجح `bash -n` للسكربتين وPython compile والتحقق من ملف workflow كـJSON/YAML subset.

المجموعات الـ20 الجديدة تغطي مسار الترحيل الكامل، 0/2/2733، وصول قبل التوقف وانحراف لاحق، CLI/JSON، hash/schema/aliases، نقص/زيادة/تبديل مجموعة الإثبات، IdentityOnly، حفظ تدقيق التنظيف، مخطط جزئي/أحدث ومقاسات متعارضة، التعافي ومقارنة أعمال أحدث، الانقطاع، تعريف الحراس، جدول أعمال إضافي، صحة الإصدار والملف وPID، إعدادات التشغيل/بصمة DLL، منع restore بعد start، وWAL حقيقي.

نتائج Bash النهائية موثقة في [سجل harness](SafeEvidenceDeploymentBashResults.log): **26 سيناريو في الجولة الكاملة، ثم 2/2 بعد إضافة فحص صلاحيات المرشح**، أحدهما رفض قبل توقف الخدمة والآخر المسار الكامل مع الفحص الجديد. يستخدم harness قواعد ملفية وأداة الصيانة الحقيقية وترحيلات EF الحقيقية. `systemctl/sudo/git/curl/build/publish` محاكاة مقيدة بجذره؛ التطبيق والأداة بُنيا ونُشرا Release فعليًا أولًا، ثم تستعمل محاكاة publish هذه الملفات المعتمدة. لا يعتبر هذا اختبار systemd أو HTTP خدمة إنتاجية. القفل في Windows محاكاة فقط. اختبارات inherited FD9 والتزامن بقفل kernel الحقيقي موجودة في harness وتعمل على Linux فقط، ولم تُنفذ محليًا هنا.

لإعادة harness على بيئة اختبار Linux مجهزة، بعد نشر التطبيق والأداة إلى TEMP:

```bash
export LARAFASHION_HARNESS_TOOL="$TEST_TMP/tool-drop/LaraFashion.IntegrationTests.dll"
export LARAFASHION_HARNESS_TOOL_DROP="$TEST_TMP/tool-drop"
export LARAFASHION_HARNESS_APP_DROP="$TEST_TMP/app-drop"
export LARAFASHION_HARNESS_COMMIT='<full-tested-sha>'
bash deploy/tests/run-harness.sh
```

هذا المثال لا يشغّل سكربت الإنتاج بإعداداته الافتراضية. كل سيناريو ينشئ root مستقلًا تحت TEMP، وعلامة harness، ومسارات mocks لجميع الأوامر المواجهة للخدمة والشبكة. توجد مهلة لكل سيناريو وحقن قتل للعملية المملوكة للاختبار فقط.

فُحص التطبيق المنشور نفسه أيضًا على Windows بوضع Production وقاعدة fixture، على `127.0.0.1:59993` فقط: GET أعاد **200 JSON دون redirect** رغم ضبط HTTPS port إلى 443. قورنت SHA وهوية DB وPID مع العملية التي تملك منفذ الاستماع، ثم اجتاز نتيجة `--deployment-health` فاحص الآلة. أُوقفت العملية بـCtrl+C وأُغلق المنفذ. startup أظهر DDL التصنيفات idempotent السابق وإنشاء مفتاح DP اصطناعي في المجلد الدائم خارج الإصدار؛ لم تُنسخ مفاتيح اختبار إلى إنتاج أو تُنفذ Checkout/مراجعة.

## G. النسخة المرجعية وحفظ الأعمال

المصدر للقراءة فقط:
`C:\Users\us050\LaraFashion-Private\size-preflight-20261005T181451Z-otg744yh\larafashion.snapshot.db`.

بصمته قبل التنفيذ وبعده:

```text
675B33E415BCB3BC1916C49C07681AE32A54C54D31E232388EA75797AFD0537D
```

الأمر الفعلي شغّل DLL Release مع `--verify-review-upgrade <reference> <sha>`؛ أنشأ نسخة عمل جديدة خارج المستودع تحت `TEMP/LaraFashionReviewUpgrade/15499096b58c4df2b2bb5a7f7bfe4c25`. نجحت مقارنة **21 جدولًا، كل الصفوف والأعمدة الأصلية، و12 trigger سابقة**؛ الإثبات **2730**، integrity `ok` وFK violations صفر. لم تنشأ قرارات مدير أو مراجعات أو حركات مخزون. نسخة SQLite الوسيطة لها بصمة مستقلة مرتبطة بالخطة؛ لا يشترط أن تساوي بصمة الملف المرجعي رغم تطابق الأعمال.

التشخيص اللاحق لنسخة العمل للقراءة فقط يثبت 2730 سطرًا مؤهلًا بالإثبات، 1074 ذا مقصد حالي، و1656 بمنتج مفقود، وصفر قرارات منفذة. سُجل ملخص هذه الأعداد فقط في سجل التسليم؛ بقي تشخيص المعرفات التفصيلي تحت TEMP.

2730/21/12 نتائج تخص هذا المرجع، وليست أعدادًا ثابتة في السكربت. لا تُستبدل بها الإنتاج أو أحدث snapshot؛ لا تزال أهلية الاستخدام تحتاج المقصد وموافقة المدير.

## H. أمثلة ملخصات آمنة

```json
{"ContractVersion":1,"Command":"--deployment-apply","Success":true,"SchemaState":"BeforeIdentity","ExpectedEvidenceCount":3,"ImportedEvidenceCount":3,"Gates":{"SchemaVerified":true,"BusinessPreserved":true,"EvidenceVerified":true,"IntegrityVerified":true,"GuardsVerified":true}}
```

هذا عرض مختصر للإنسان؛ تقرير الآلة الكامل يتضمن commit وبصمات المجموعة والنسخة وحقول العقد الأخرى. المثال المختصر نفسه لا يجتاز فاحص النشر.

```json
{"ContractVersion":1,"Success":false,"FailureCode":"EvidenceSetMismatch"}
```

بعد تشغيل محتمل: `MANUAL_RECOVERY_REQUIRED: start attempted; current DB retained.` لا أسماء زبائن أو عناوين أو أسطر إثبات مفصلة في الملخص. تفاصيل الخطط والسجلات المحلية تحت مجلد المحاولة المحمي.

## I. تجهيز الخادم المطلوب لاحقًا — لم يُنفذ

1. اعتمد نتيجة harness على Linux الفعلية، خصوصًا FD9، التزامن، `fstat/fsync`، قتل العملية والتعافي. تحقق من نسخة SDK المستهدفة وحزم runtime والأدوات الموجودة وصلاحيات المستخدم والمساحة.
2. حافظ على المسارات المرجعية القابلة للتهيئة: repo `/var/www/larafashion/LaraFashion`، live `publish`، DB `data/larafashion.db`، uploads، releases، backups، الخدمة `larafashion` والقفل `/tmp/larafashion-production-deploy.lock`.
3. جهز إعداد نشر root-owned غير قابل للكتابة من group/others في `/etc/larafashion-deploy.env`، دون shell tracing، أو قيم البيئة المعتمدة. المجلدات الدائمة/الويب/المصدر/الإصدارات/الأدلة والمفاتيح يجب ألا تتداخل.
4. جهز `LARAFASHION_SERVICE_ENV_FILE` خاصًا خارج حزمة الموقع والمصدر وuploads. يجب أن تستعمله الخدمة كـEnvironmentFile الوحيد الذي يفحصه السكربت. الصيغة المدعومة `KEY=value` أو قيمة كاملة مقتبسة، دون duplicate keys أو continuation. لا تنسخ أسرار المشروع أو إعدادات fixture.
5. ضع فيه `ASPNETCORE_ENVIRONMENT=Production`، مسارات DB/uploads/DP المطلقة، وإعدادات JWT الحالية `JwtSettings__SecretKey/Issuer/Audience/ExpiryMinutes`، وانقل أي إعدادات تشغيل أخرى معتمدة من إعداد الخدمة الحالي دون طباعة قيمها. ملفات appsettings لا تنتقل في publish.
6. جهز مسار `LARAFASHION_DATA_PROTECTION_PATH` دائمًا موجودًا خارج الإصدار والويب، مع صلاحيات الخدمة. حافظ على key ring الحالي وعلى application discriminator/content root المستخدم لتذاكر المراجعة؛ لا مفاتيح اختبار ولا تدوير أو SetApplicationName جديد ضمن هذه المهمة.
7. سجل جميع خدمات الكتابة المعروفة في `LARAFASHION_WRITER_SERVICES`، بما فيها الخدمة الرئيسية والعمليات الخلفية ذات الصلة. الخدمة تحتاج WorkingDirectory ثابتًا للـlive وMainPID للعملية التي تجيب على `/health`. يتطلب الفحص `fuser` وsudo محدودًا للخدمات المعتمدة؛ لا قتل مجهول. يجب أن يستطيع مستخدم الخدمة عبور المجلدات الدائمة وقراءة DLL داخل staging الخاص؛ يرفض السكربت الإخفاق قبل stop. جهز علاقة مستخدم النشر بالخدمة أو ACL معتمدًا دون توسيع صلاحيات الأدلة والمفاتيح.
8. إن كانت القاعدة IdentityOnly، اعتمد نسخة سابقة للهوية خارج الويب بمسار `LARAFASHION_PRE_IDENTITY_EVIDENCE_PATH` وSHA256 في `LARAFASHION_PRE_IDENTITY_EVIDENCE_SHA256`. المصدر محمي من retention ولا يُستبدل بـbackup أحدث بعد الهوية.
9. اعتمد ملف توافق موثق للإصدار السابق: ContractVersion/Commit/Migration/ApplicationSha256. لا تنشئه بالتخمين لإتاحة rollback؛ إن غاب فالتوقف اليدوي هو النتيجة الآمنة.

الـseeder السابق بقي كما هو: قد ينفذ DDL idempotent للتصنيفات، وقد ينشئ حساب الإدارة الافتراضي الموجود أصلًا إذا كان جدول الإدارة فارغًا. وجود مدير معتمد ومراجعة هذه التهيئة من تجهيز الخدمة. مقارنة الأعمال الأساسية تتم **قبل startup**؛ بدء التشغيل ليس مرحلة مضمونة بلا كتابة، وفشلها لا يجيز restore قديمة.

لا توجد سياسة حذف بالعمر/العدد للمحاولات أو مصادر الإثبات أو أزواج التعافي. التنظيف لاحقًا يحتاج مراجعة مستقلة تحدد الأدلة التي لم تعد مطلوبة. ملف sentinel باسم فيه مسافات بقي محفوظًا في الاختبارات.

## J. التشغيل المستقبلي — منفصل عن التنفيذ المحلي

بعد معالجة فجوة Linux وتجهيز الخادم واعتماد نشر محدد، تُراجع شجرة التغييرات ويُجري المستخدم commit/push. الـworkflow يختبر `github.sha` نفسه؛ فشل validate يمنع SSH. يحتفظ wrapper بالقفل ويجهز checkout ذلك SHA ثم يستدعي **المسار الوحيد**:

```bash
bash deploy/deploy-production.sh '<full-tested-sha>'
```

الاستدعاء أعلاه **لم يُنفذ** هنا بإعدادات إنتاج. يبني السكربت التطبيق والأداة Release قبل stop، ويمرر اتصال EF design-time إلى ملف خاص بالمحاولة بوضع ReadWrite؛ لا startup أو database update للبناء. يطبق المخطط الفعلي على القاعدة المستهدفة نفسها بعد التجربة، ولا يرفع قاعدة rehearsal مكانها.

فحص الصحة GET loopback بعقد JSON وSHA وMigration وهوية ملف DB وMainPID، دون redirect أو `--insecure` أو Checkout اصطناعي. `Completed` يتطلب صحة النسخة المرشحة بعد المقارنة السابقة للتشغيل. راجع ملفات المحاولة محليًا عند الفشل؛ الحالة غير المغلقة تحتاج معالجة مستقلة قبل إعادة التنفيذ.

## K. فجوات الاعتماد

**قبول سكربت Linux لم يكتمل.** محاكاة flock على Windows لا تثبت توريث FD9 أو تزامن عمليتين أو عدم deadlock على kernel Linux. كما لم يُختبر `fstat/fsync` وsystemd وHTTP التطبيق على Linux أو الخادم الفعلي؛ فحص GET المنشور كان محليًا على Windows فقط. بوابة Linux موجودة في Actions لكنها لم تُشغل من هذه المهمة، ولا يُستبدل نجاحها بمراجعة النص أو `bash -n`.

تجهيز EnvironmentFile ومفاتيح DP وكتّاب الخدمة وتوافق الإصدار السابق خارج المتاح محليًا. السكربت يتوقف إذا لم يستوفِ هذه المتطلبات. ليس في التقرير إعلان نجاح نشر أو جاهزية جميع الطلبات للتسوية.

## L. حدود التنفيذ المؤكدة

لا Commit أو Push أو تغيير فرع أو Reset، ولا تشغيل Actions أو Deployment أو SSH أو استعمال مفتاح خاص. لم تُفتح قاعدة المشروع الأصلية عبر أدوات الصيانة ولم تُعدل، ولم يُتصل بالإنتاج أو تُوقف خدمة حقيقية. المرجع بقي ببصمته. لم تُثبت/تحدث حزم أو إطار أو أدوات نظام، ولم تُرفع قواعد أو إثبات تفصيلي أو بيانات زبائن إلى خدمة خارجية. جميع القرارات والكتابات واختبارات الفشل كانت على fixtures منفصلة فقط.
