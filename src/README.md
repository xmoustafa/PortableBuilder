# PortableBuilder V1.1 — GUI + قاعدة بيانات تطبيقات

**Developed by : Moustafa**

> استخدم الأداة مع البرامج المسموح لك بتغليفها/نقلها فقط. ما تعملش بها كراك أو إعادة توزيع لبرامج مدفوعة.

## الجديد

- **GUI بسيط**: اختار البرنامج من قائمة → راجع المسارات → Build.
- **قاعدة بيانات 243 تطبيق/لانشر** في `apps.json` — **100+ لعبة/لانشر** و**100+ برنامج مشهور**:
  - Steam, Epic, Battle.net, Riot, Rockstar, Ubisoft, EA, GOG, Amazon, Bethesda, Paradox, Wargaming, HoYoPlay, WeGame, VK Play, Roblox, FiveM, Minecraft, Prism, CurseForge, Modrinth, Lunar Client, Jagex, RuneLite, Google Play Games…
  - Chrome, Firefox, Discord, Slack, Teams, Zoom, Telegram, VLC, OBS, GIMP, Blender, Photoshop, DaVinci Resolve, VS Code, Visual Studio, JetBrains, Python, Node.js, Docker, LibreOffice, Acrobat, Dropbox, OneDrive, NordVPN, Razer, Logitech, Corsair…
- **كشف تلقائي** من:
  - مسارات التثبيت المعروفة في القاعدة.
  - مفاتيح `Uninstall` في الريجستري (`DisplayName`, `InstallLocation`, `DisplayIcon`).
  - مجلدات `%LOCALAPPDATA%`, `%APPDATA%`, `%ProgramData%`, `LocalLow`.
  - مفاتيح `HKCU\Software` و`HKLM\SOFTWARE` والخدمات.
- **بناء مباشر** (من غير Snapshot) للتطبيقات المعروفة: أسرع وأنضف.
- **بناء من الـ GUI فقط** — مفيش CLI ولا Snapshot/Diff من سطر الأوامر.
- **Copy mode** للفلاشات FAT32/exFAT، و**Junction** للـ NTFS.

---

## التشغيل السريع (GUI)

### الواجهة

- **الشريط العلوي**: `Refresh` / `Browse EXE...` / `Browse Folder...` / عدد التطبيقات / `Build Package` / `Open`.
- **Build options**: Output folder + Mode + Import registry / Wait for exit / Restore on exit / **Best Portable layout** (مفعّل افتراضيًا) / **Deep registry** (مفعّل افتراضيًا).
- **قائمة التطبيقات**: Application / Category / Install path.
- **Selected application**: Install folder / Main EXE / Data folders / Registry keys / Services / Arguments.
- أي رسائل أو progress بتظهر في شريط الحالة أسفل الواجهة؛ مفيش ملف log بيتم إنشاؤه.

1. شغّل:

```text
PortableBuilder.exe   (في أي فولدر، مفيش مسار ثابت)
```

2. استنى ثانية أو ثانيتين لحد ما القائمة تظهر. الأداة بتبني Index من الريجستري وAppData وتقارنه بقاعدة البيانات.
3. اختار البرنامج (مثلًا Steam أو Chrome).
4. راجع:
   - **Install folder**
   - **Main EXE**
   - **Data folders** (كل واحد في سطر)
   - **Registry keys** (كل واحد في سطر)
   - **Services**
5. اختار **Output folder** (افتراضيًا على الديسكتوب باسم `<App>Portable`).
6. اختار Mode:
   - `Junction (NTFS)` — الأسرع.
   - `Copy (USB/FAT32)` — ينسخ الملفات، يشتغل على أي فلاشة.
   - **Best Portable layout** (مفعّل افتراضيًا) — Symlink + `<App>Data` + `<App>.exe`. لما يكون مفعّل، خيار Mode بيتقفل.
7. اضغط **Build Package** من الشريط العلوي.
8. افتح الفولدر وشغّل `<App>.exe` (أو `PortableLauncher.exe` لو Best Portable layout متوقف).

### لو البرنامج مش في القائمة

- اضغط **Manual Search...** واكتب اسم البرنامج (أو اختار من القائمة). الأداة بتدور في:
  - قاعدة البيانات (الـ 243 تطبيق) + سجل Uninstall.
  - مجلدات AppData/ProgramData اللي اسمها قريب من الاسم.
  - مفاتيح الريجستري والخدمات اللي اسمها قريب من الاسم.
- لو لقى حاجة، بيحطها في حقول المسارات. لو ملقاش، بيخليك تحط المسارات يدويًا (Install folder / EXE / Data / Registry / Services) وتضيفه.
- بديل أسرع: اضغط **Folder...** واختار فولدر التثبيت، أو **EXE...** واختار الملف الرئيسي.
- عدّل أي مسار ناقص/زائد في الصناديق قبل البناء.

### البحث اليدوي

استخدم زر **Manual Search...** في الواجهة؛ بيدوّر بالاسم في قاعدة البيانات + Uninstall + AppData + الريجستري + الخدمات.

---

## قاعدة البيانات `apps.json`

الملف جنب `PortableBuilder.exe` وقابل للتعديل. لو مش موجود، الأداة بتستخدم نسخة مدمجة في الـ EXE.

> **متى تكبر القاعدة؟** الفحص/الكشف **لا** يعدّل `apps.json` تلقائيًا. القاعدة تكبر فقط لما تضغط **Save to DB** لبرنامج. الحفظ بيتم في `apps.json` جنب الـ EXE + بيتزامن مع `src\apps.json`.
>
> **Auto-save**: فعّل خيار **Auto-save detected** في الـ GUI لحفظ كل البرامج المكتشفة تلقائيًا عند الفحص.
>
> **تخصيص شخصي**: حط ملف `apps.local.json` جنب الـ EXE (بنفس شكل `apps.json`) فيه إدخالاتك — بيتدمج فوق القاعدة المدمجة (overrides by id/name) من غير ما تعدّل الـ EXE.

شكل الإدخال:

```json
{
  "id": "steam",
  "name": "Steam",
  "category": "Game Launcher",
  "publisher": "Valve",
  "aliases": ["Valve Steam"],
  "tokens": ["steam", "valve"],
  "installPaths": ["%ProgramFiles(x86)%\\Steam", "%ProgramFiles%\\Steam"],
  "exe": "steam.exe",
  "dataPaths": ["%LOCALAPPDATA%\\Steam", "%APPDATA%\\Steam"],
  "registryKeys": [
    "HKCU\\Software\\Valve\\Steam",
    "HKLM\\SOFTWARE\\WOW6432Node\\Valve\\Steam"
  ],
  "processNames": ["steam"],
  "excludes": ["steamapps", "logs", "dumps", "depotcache"],
  "args": "",
  "notes": "Steam games with Steam DRM still need Steam running."
}
```

- `installPaths`: مسارات محتملة لمجلد التثبيت (بتدعم متغيرات البيئة).
- `exe`: الملف الرئيسي. لو مش موجود، الأداة بتدور على أكبر EXE مناسب.
- `dataPaths`: مجلدات/ملفات الإعدادات.
- `registryKeys`: مفاتيح كاملة أو مختصرة (`HKCU\...`).
- `excludes`: أسماء مجلدات يتم تخطيها أثناء نسخ مجلد التثبيت.
  - **Steam** مثلًا بيستثني `steamapps` عشان ما ينسخش مكتبة الألعاب (ممكن تكون مئات الجيجابايت).
- `processNames`: اسم العملية (للعرض/الاختيار، مش بيتم استخدامه تلقائيًا في `WaitProcess`).

### إضافة تطبيق جديد

1. افتح `apps.json`.
2. انسخ إدخال موجود وعدّل `id`, `name`, `installPaths`, `exe`, `dataPaths`, `registryKeys`.
3. احفظ، وشغّل `PortableBuilder.exe` واضغط **Refresh**.

---

## إزاي الكشف بيشتغل؟

1. **المسارات المعروفة**: يختبر كل `installPaths`؛ أول واحد موجود بيتستخدم.
2. **Uninstall registry**: لو المسارات مش موجودة، يدور في:
   - `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`
   - `HKLM\SOFTWARE\WOW6432Node\...\Uninstall`
   - `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall`
   - يقرأ `DisplayName`, `InstallLocation`, `DisplayIcon`, `UninstallString`.
3. **Main EXE**: يدور على اسم `exe` في فولدر التثبيت (بدون دخول في Junction)، وإلا يستخدم `DisplayIcon`، وإلا أكبر EXE مناسب.
4. **Data folders**: مسارات القاعدة + فولدرات AppData/ProgramData اللي اسمها يطابق اسم/alias التطبيق.
5. **Registry**:
   - **Fast**: مفاتيح القاعدة + `HKCU\Software`/`HKLM\SOFTWARE` (depth 4) بالاسم.
   - **Deep** (مفعّل افتراضيًا): يمسح `HKCU\Software` و`HKLM\SOFTWARE` (depth 5) + Uninstall + App Paths + RegisteredApplications + Classes + Services، **ويقرأ قيم النصوص** كمان. يعني بيلقط مفتاح زي `HKCU\Software\Vendor\Shared` لو جواه قيمة فيها اسم البرنامج أو مساره.
    - الخيار ده من الواجهة: **Deep registry**.
6. **Services**: أسماء الخدمات اللي تطابق اسم التطبيق.
7. **استبعادات أمان**: `Windows`, `WindowsApps`, `Package Cache`, `EdgeWebView`, `Temp`, `Installer` لتقليل النتائج الكاذبة.
8. **للبرامج الغريبة**: استخدم **Manual Search** أو **Browse Folder** وراجع المسارات يدويًا قبل البناء.

> الـ Deep scan بياخد حوالي 8–15 ثانية على جهاز متوسط. لو عايز أسرع، شيل علامة **Deep registry**.

---

## التشغيل من أي مكان / أي Drive

- فولدر الباكدج نفسه **ممكن يكون في أي مكان** (Desktop، D:، E:، فلاشة…) لأن المسارات جوه الباكدج نسبية (`SteamData\...`)، والـ EXE بيحسب `BaseDirectory` بنفسه.
- **Best Portable layout** بيستخدم Symlink (Admin) افتراضيًا، أو **Best + Junction** لو فعّلت الخيار ده (لازم NTFS محلي، من غير Admin). **Normal layout + Junction** لازم NTFS محلي؛ على FAT32/exFAT استخدم **Copy mode** أو Symlink. **Roblox وFiveM بيفرضوا Symlink** لأنهم على شير شبكة.
- لو غيّرت حرف الـ Drive (E: → F:)، اللانشر بيعيد إنشاء اللينكات للمسار الجديد أول ما تشغله — بس اقفل البرنامج الأول.
- **مهم**: لو البرنامج نفسه متثبت على مسار مطلق زي `D:\Programs\...`، اللانشر بيحتاج نفس المسار على الجهاز التاني. الـ Builder بيطبع تحذير `[!] Absolute path...` في اللوج. الحل: ثبّت البرنامج على C: أو عدّل المسارات في `.reg`/`launcher.ini`.
- الريجستري والخدمات بتتكتب على نظام الجهاز المضيف، مش جوه الفولدر.

---

## الباكدج الناتج

### Best Portable layout (الافتراضي — علامة «Best Portable layout» مفعّلة)

```text
SteamPortable\
  Steam.exe                  ← لانشر مدمج فيه الإعدادات (مفيش launcher.ini)
  SteamData\
    AppData\Local            ← %LOCALAPPDATA%\Steam
    AppData\Roaming          ← %APPDATA%\Steam
    ProgramData              ← %ProgramData%\Steam
    ProgramFiles             ← C:\Program Files\Steam
    ProgramFiles(x86)        ← C:\Program Files (x86)\Steam
    Regs\Steam.reg           ← كل مفاتيح الريجستري في ملف واحد
```

- اللانشر اسمه اسم البرنامج (`Steam.exe`) مش `PortableLauncher.exe`، **وبياخد أيقونة البرنامج الأصلي** تلقائيًا (بأعلى دقة 256×256).
- الإعدادات **مدمجة جوه الـ EXE** (مضغوطة + مشفّرة)، فمفيش `launcher.ini` ظاهر ومفيش `changes.txt`.
- `LinkType=Symlink` و`RequireAdmin=1` — محتاجين Admin.
- `WaitForExit=0` و`RestoreOnExit=0` و`DelayMs=2000` — اللانشر بيقفل بعد التشغيل واللينكات بتفضل.
- الريجستري `SteamData\Regs\Steam.reg` واحد (مدمج من كل المفاتيح).
- الـ EXE بيتولّد وقت البناء من `PortableLauncher\Program.cs` + إعدادات مدمجة.
- لو `csc.exe` أو `PortableLauncher\Program.cs` مش موجودين، بيرجع تلقائيًا لـ `PortableLauncher.exe` + `launcher.ini`.

مثال اللينكات اللي بتتعمل وقت التشغيل:

```text
%LOCALAPPDATA%\Steam         ->  SteamData\AppData\Local
%APPDATA%\Steam              ->  SteamData\AppData\Roaming
%ProgramData%\Steam          ->  SteamData\ProgramData
C:\Program Files\Steam       ->  SteamData\ProgramFiles
C:\Program Files (x86)\Steam ->  SteamData\ProgramFiles(x86)
```

### GameLoop / PUBG

- الـ DB بتحتوي على المفاتيح الثلاثة:
  - `HKLM\SOFTWARE\WOW6432Node\Tencent`
  - `HKCU\SOFTWARE\Tencent`
  - `HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\MobileGamePC`
- بيتم دمجهم في `GameLoopData\Regs\GameLoop.reg` (Best Portable layout).
- بيتم إعادة كتابة المسارات:
  - `D:\PUBG TEST\TxGameAssistant` → `D:\Program Files\TxGameAssistant`
  - `D:\Temp\TxGameDownload` → `E:\Temp\TxGameDownload`
  - `D:\Temp` → `E:\Temp`
- لو `UI\AndroidEmulatorEn.exe` موجود، الأداة بتولّد كمان:
  ```text
  PUBG Mobile.exe
  ```
  بأيقونة `AndroidEmulatorEn.exe`، وبيشغّل:
  ```text
  AndroidEmulatorEn.exe -vm 100 -engine aow -startpkg com.tencent.ig
  ```
  مع نفس اللينكات والريجستري والخدمات — يعني مش محتاج `pubg.bat`.
- على الجهاز التاني اتأكد من وجود:
  ```text
  D:\Program Files\TxGameAssistant
  E:\Temp
  ```
  أو عدّل المسارات في `GameLoopData\Regs\GameLoop.reg`.
- لو عندك `PUBG.ico` أو `PUBGM.ico`، حطه جنب `AndroidEmulatorEn.exe` (أو في `UI`) و`PUBG Mobile.exe` هياخده كأيقونة.

### FiveM (split layout — تطبيق محلي + داتا لكل جهاز)

- المسار الوحيد: `%LOCALAPPDATA%\FiveM`.
- **FiveM Portable Setup** بيولّد:
  ```text
  FiveM.exe              ← يعمل/يتأكد من لينك الداتا ويشغّل FiveM
  Enable_Sharing.exe     ← اختيار الشير + تجهيز التطبيق محليًا ولينك الداتا
  Uninstall.exe          ← إزالة لينك داتا الجهاز (التطبيق المحلي بيفضل)
  FiveM\                 ← seed للتطبيق (بدون الداتا الثقيلة)
  ```
- **التطبيق بيفضل محلي على كل جهاز:**
  ```text
  %LOCALAPPDATA%\FiveM\
    FiveM.exe
    FiveM.app\bin / citizen / mods / plugins
    CitizenFX.ini                     ← محلي (مسار GTA V لكل جهاز)
    FiveM.app\data  →  Symlink
  ```
- **الداتا فقط هي اللي على الشير، ولكل جهاز مجلد مستقل:**
  ```text
  <share>\Data\Local\<COMPUTERNAME>\FiveM\data
  ```
  وجواها:
  - `cache` — browser/subprocess/extracted shaders
  - `server-cache` — resources والخرايط اللي بتتحمّل
  - `server-cache-priv` — private server resources
  - `game-storage` — GTA V game data
  - `nui-storage` — تخزين واجهات السيرفر
- الـ seed بيستبعد `FiveM.app\data` و`CitizenFX.ini` عشان كل جهاز ينزّل موارده بنفسه ويظبط مسار GTA V بتاعه.
- أول تشغيل: لو مفيش FiveM محلي → ينسخ الـ seed محليًا. لو فيه `FiveM.app\data` محلية → بيتم Adopt لها على الشير. لو فيه نسخة قديمة (لينك الفولدر كامل) → بتتحوّل تلقائيًا للـ split layout.
- التحديثات بتاعة تطبيق FiveM بتتم محليًا على كل جهاز، لكن **المقاطع/الموارد/الشيدرز مش بتتشارك بين الأجهزة** — كل جهاز معزول في الداتا بتاعته.
- `Uninstall.exe` بيشيل **لينك `FiveM.app\data` فقط**، بيسيب التطبيق المحلي، وبياخد اختيار حذف داتا الجهاز من الشير.

### Normal layout (لو شلت علامة «Best Portable layout»)

```text
MyAppPortable\
  MyApp.exe                  ← لانشر مدمج فيه الإعدادات + أيقونة البرنامج
  Data\
    Links\Install\...      ← Junction/Symlink لمجلد التثبيت
    Links\Data\...         ← Junction/Symlink لمجلدات البيانات
    Copy\...               ← Copy mode
  Regs\
    reg_0.reg              ← مفاتيح الريجستري
    export_0.reg ...       ← إعدادات البرنامج وقت الخروج
```

> في Normal layout كمان بقى بيطلع `<App>.exe` بأيقونة البرنامج، و`launcher.ini` بيظهر فقط لو فشل توليد الـ EXE (fallback). مفيش `changes.txt` ولا `launcher.log` (سايلنت).
### `launcher.ini` (Fallback فقط)

```ini
[App]
Name=Steam
LinkType=Symlink
RequireAdmin=1
Silent=1
WaitForExit=1
RestoreOnExit=1

[Folders]
%ProgramFiles(x86)%\Steam=Data\Links\Install\Steam
%LOCALAPPDATA%\Steam=Data\Links\Data\0\Steam

[CopyFolders]
[Overlay]
[Registry]
reg0=Regs\reg_0.reg
export0=Regs\export_0.reg

[RegistryExport]
HKEY_CURRENT_USER\Software\Valve\Steam=Regs\export_0.reg

[Services]
Steam Client Service="...\SteamService.exe" /service|Steam Client Service|...

[Run]
%ProgramFiles(x86)%\Steam\steam.exe=
```

- **Junction**: أسرع، NTFS محلي فقط. **Symlink**: محتاج Admin وNTFS.
- **CopyFolders**: للفلاشات، بينسخ المجلد كامل ويحفظ التغييرات عند الخروج.
- **Overlay**: لملفات مفردة مش جوه فولدر خاص بالبرنامج.
- **Registry**: `import` عند البدء و`export` عند الخروج.
- **Services**: user-mode services فقط. Kernel drivers بتتخطى.

---

## CLI (اتشال)

**اتشال خلاص.** الأداة بقت **GUI فقط** — مفيش `PortableBuilderCLI.exe` ولا Snapshot/Diff من سطر الأوامر.

لو محتاج تعمل build، استخدم الواجهة:

1. اختار البرنامج من القائمة (أو Manual Search).
2. راجع المسارات.
3. اضغط **Build Package**.

---

## البناء

```bat
cd <folder>\src
build.cmd
```

بيبني:

- `PortableBuilder.exe` — GUI فقط.

المتطلبات: Windows + .NET Framework 4.8. مش محتاج Visual Studio.

> اقفل الـ GUI قبل إعادة البناء لأن الـ EXE بيكون مقفول.

### التوقيع (Code signing)

الـ EXE بيطلع **بدون توقيع**، فأول تشغيل على جهاز تاني بيظهر تحذير SmartScreen. الحل:

```bat
:: شهادة تجريبية (self-signed) للاختبار المحلي:
powershell -ExecutionPolicy Bypass -File make-cert.ps1
:: التوقيع:
sign.cmd portablebuilder-test.pfx test
```

- `sign.cmd <cert.pfx> <password>` بيوقّع `PortableBuilder.exe` بـ signtool + timestamp.
- **شهادة self-signed مش بتشيل تحذير SmartScreen** على الأجهزة التانية.
- للإزالة الكاملة، اشترِ **OV/EV code-signing certificate** ووقّع بيها.
- الـ EXE فيه Version info دلوقتي (`1.1.0.0` + ProductName + `Cyber Misr | سايبر مصر`).

> ملاحظة: التشويش (ConfuserEx) والتشفير (XOR/GZip) اتشالوا بالكامل عشان برامج الحماية (Defender/AV) ما تعلّمش الـ EXE. البناء بقى عادي بدون تشويش، وقاعدة البيانات مدمجة كنص عادي (`apps.json`).

### النشر على GitHub + التحديث التلقائي

المشروع مجهز بـ GitHub Actions وUpdater مدمج:

1. اعمل **Public repository** على GitHub (الريبو الخاص يحتاج Token آمن، والـ updater الحالي بدون Token).
2. ارفع محتويات المشروع (مجلد `src` + `app.ico` + `apps.json` + `.github` + `.gitignore`).
3. أول إصدار:
   ```bat
   git init
   git add .
   git commit -m "PortableBuilder V1.1"
   git branch -M main
   git remote add origin https://github.com/USERNAME/REPO.git
   git push -u origin main
   git tag v1.1.0
   git push origin v1.1.0
   ```
4. `Release PortableBuilder` هيبني الـ EXE على GitHub وينزل Release فيها:
   ```text
   PortableBuilder.exe
   PortableBuilder.exe.sha256
   ```
5. أي تحديث بعد كده: ارفع تعديلاتك على `main` الأول، وبعدها اعمل tag جديد أعلى من الحالي:
   ```bat
   git add .
   git commit -m "Update"
   git push origin main
   git tag v1.1.1
   git push origin v1.1.1
   ```
   الـ workflow هيبني الإصدار الجديد تلقائيًا.

**إزاي التحديث بيوصل للمستخدم؟**
- البرنامج بيفحص آخر Release من GitHub عند التشغيل (بحد أقصى مرة كل 6 ساعات).
- لو لقى إصدار أحدث، بيعرض رسالة فيها رقم الإصدار وملاحظات الإصدار.
- لو المستخدم وافق: يحمّل `PortableBuilder.exe` + ملف SHA-256، يتحقق من البصمة، ثم يبدأ نسخة التحديث.
- النسخة الجديدة تنتظر خروج البرنامج القديم، تستبدل الـ EXE، وتعيد فتح البرنامج.
- لو البرنامج مركّب في `Program Files`، النسخة الجديدة تطلب UAC تلقائيًا.
- ملف `src/UpdateConfig.cs` فيه placeholders، والـ workflow بيستبدلها بـ `github.repository` قبل البناء — مش محتاج تكتب اسم الريبو بنفسك.

> مهم: أول Release لازم يكون فيه `PortableBuilder.exe` و`PortableBuilder.exe.sha256`.  
> لو الـ `.sha256` ناقص، الأداة بتلغي التحديث حفاظًا على الأمان.  
> لو الريبو Private، التحديث التلقائي محتاج Token آمن أو وسيط؛ الأفضل يبقى Public.

### حدود مهمة

- **مش سحر**: الأداة بتعتمد على قاعدة البيانات + كشف الريجستري/AppData. أي برنامج غريب أو مسار مخصص ممكن يحتاج تعديل يدوي.
- **الريجستري مش معزول**: `reg import/export` على ريجستري الجهاز. مفيش virtualization كامل.
- **Kernel drivers** لا يمكن جعلها portable. الخدمات user-mode فقط.
- **Steam games**: الـ `steamapps` مستثنى من نسخ Steam (مكتبة الألعاب + `userdata` بتاعة الـ saves/screenshots). الـ login/config (`config\loginusers.vdf`) بيتنسخ. الألعاب اللي فيها Steam DRM مش هتشتغل من غير Steam أونلاين.
- **UWP/MSIX** (Xbox app, WhatsApp Store, Edge WebView) غير مدعومة.
- **البرامج اللي بتتحقق من التوقيع/المسار/الترخيص** ممكن ترفض.
- **Admin**: Program Files / ProgramData / HKLM / Services محتاجة UAC. للصامت استخدم Task Scheduler بـ Highest privileges.
- **Best Portable layout** بيحتاج Admin (Symlink) + وجود `csc.exe` وقت البناء. لو مش موجود، بيرجع لـ `PortableLauncher.exe` + `launcher.ini`.
- **Best Portable layout** بيسيب اللينكات موجودة بعد القفل (`RestoreOnExit=0`). لو عايز تنظيف، استخدم Normal layout أو غيّر الإعداد.
- **الريجستري والخدمات** برّه الفولدر في الحالتين.
- **Copy mode** أبطأ للأحجام الكبيرة.
- **Long paths** > 260 حرف ممكن تفشل على .NET Framework 4.8.
- **الواجهة**: لو ظهرت النافذة مقطوعة على شاشة عالية الدقة، كبّرها أو حرّكها؛ الأداة بتستخدم layout متجاوب.
- **قاعدة البيانات إرشادية**: راجع الإعدادات (`launcher.ini` لو ظهر كـ fallback) قبل التوزيع.

### حالات معروفة (مش Bugs) — اعرفها قبل ما تنقل

| الحالة | معناها |
|---|---|
| **الألعاب والـ Saves** | `steamapps` (ويشمل `userdata`) مش بيتنسخ. لو عايز Saves/سكرينشوتات لعبة معينة، ضيف فولدرها يدويًا أو استخدم خيار إضافة لعبة. |
| **Admin + NTFS + 64-bit** | Symlink وخدمات مثل Steam محتاجين Admin وقرص NTFS، و`%ProgramFiles(x86)%` محتاج ويندوز 64-bit. |
| **أول تشغيل** | البرنامج ممكن يعمل update/repair ويكتب في مساره (اللي هو Symlink للباكدج) — طبيعي. |
| **برنامج مثبت بنفس المسار** | اللانشر بيعمل Backup للفولدر الحقيقي (`*.portable-backup-*`) وبحوّله Symlink. لو عايز ترجّع الأصل، شوف قسم "الاسترجاع". |
| **Deep scan** | بياخد 8–15 ثانية وحد أقصى 150,000 مفتاح ريجستري. للبرامج الغريبة استخدم `capture`. |
| **توليد `<App>.exe`** | بيستخدم `csc.exe` (جزء من .NET Framework 4.8). لو مش موجود، fallback تلقائي. |
| **ريجستري + خدمات** | بتتكتب على نظام الجهاز المضيف، مش جوه الفولدر. |
| **DRM** | ألعاب Steam/EA/Riot/Ubisoft اللي فيها DRM محتاجة اللانشر/login/أونلاين. |

### الاسترجاع (لو شغّلت Best layout على جهاز فيه التطبيق)

اقفل البرنامج وشغّل كـ Admin:

```bat
rmdir "<المسار الحقيقي>"
ren "<المسار الحقيقي>.portable-backup-*" <اسم الفولدر>
```

لو عايز تتابع الخطوات، فعّل اللوج مؤقتًا: حط `LogFile=launcher.log` في إعدادات الباكدج (افتراضيًا اللوج متوقف).

### إلغاء التثبيت (Uninstall)

كل باكدج بيتولّد معاه **`Uninstall_<App>.exe`** جنب `<App>.exe`. شغّله كـ Admin وهيو:

- يشيل الـ Symlinks/Junctions اللي اتعملت في `C:` (ويرجّع أي `*.portable-backup-*` مكانه).
- يوقف ويحذف الخدمات اللي الباكدج عملها.
- يحذف مفاتيح الريجستري اللي اتعمل لها import (من ملفات `.reg` جوه الباكدج).
- يسألك لو عايز يحذف فولدر الباكدج كمان (Yes = يحذفه، No = يسيبه).

> اقفل البرنامج الأول. اللينكات/الخدمات/الريجستري محتاجة Admin (UAC).

---

## استكشاف الأخطاء

| المشكلة | الحل |
|---|---|
| التطبيق مش ظاهر | استخدم **Folder...** أو **EXE...** يدويًا؛ أو أضف `installPaths` في `apps.json` |
| Main EXE غلط | عدّل `Main EXE` أو `[Run]` في `launcher.ini` |
| ناقص AppData | أضف المسار في صندوق Data folders أو `[Folders]` |
| ناقص ريجستري | أضف المفتاح في `Registry keys` أو `[Registry]` |
| فشل Junction | الباكدج على FAT32/شبكة → استخدم Copy mode |
| Service مش بيبدأ | شغّل كـ Admin؛ راجع `binPath` |
| الإعدادات مش بتتحفظ | أضف المفتاح في `[RegistryExport]` وتأكد `WaitForExit=1` |
| UAC كل مرة | Task Scheduler بـ Highest privileges |
| برنامج بيقفل اللانشر بسرعة | أضف `WaitProcess=اسم العملية` في `launcher.ini` |

---

## ملف واحد فقط

`PortableBuilder.exe` دلوقتي **ملف واحد** فيه:

- قاعدة البيانات `apps.json` (مدمجة).
- كود اللانشر `PortableLauncher\Program.cs` (مدمج).
- اللانشر العام `PortableLauncher.exe` (مدمج كـ fallback).
- أيقونة البرنامج `app.ico`.
- الواجهة + الكشف + البناء.

يعني تقدر تنقله لأي فولدر لوحده وتشغّله، من غير `apps.json` ولا فولدر `PortableLauncher`.

> لتوليد `<App>.exe` لكل برنامج، الأداة بتستخدم `csc.exe` الموجود مع .NET Framework 4.8. لو مش موجود، بترجع للانشر العام المدمج + `launcher.ini`.

## `PortableLauncher` فولدر بيعمل إيه؟

- فيه **كود اللانشر** (`Program.cs`) + `EmbeddedConfig.cs` + اللانشر العام.
- `PortableBuilder` بيستخدمه وقت البناء عشان يترجم `<App>.exe` بإعدادات البرنامج وأيقونته.
- في النسخة Single-file، الكود واللانشر **مدمجين جوه `PortableBuilder.exe`**، فالفولدر **اختياري** — تقدر تسيبه للتطوير/الرجوع لنسخة أقدم.
- الباكدج الناتج مش محتاج الفولدر ده؛ كل `<App>.exe` مستقل.

---

## الملفات

```text
PortableBuilder\
  PortableBuilder.exe       ← ملف واحد (GUI + DB + launcher مدمج + icon)
  apps.json                 ← نسخة قابلة للتعديل (اختيارية)
  app.ico / app_icon.png    ← أيقونة البرنامج
  Engine.cs                 ← الكشف + البناء + Best Portable layout
  Gui.cs / GuiProgram.cs    ← الواجهة
  app.manifest
  build.cmd
  README.md

PortableLauncher\           ← اختياري (مصدر اللانشر/الـ fallback)
  PortableLauncher.exe
  Program.cs
  EmbeddedConfig.cs
  build.cmd
  README.md
```
