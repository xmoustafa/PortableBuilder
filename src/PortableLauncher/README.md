# Portable Launcher — شرح البديل العام

## 1) الفكرة الأساسية

مفيش "بورتابل" حقيقي في معظم التطبيقات. اللانشر بيعمل **خدعة إعادة توجيه (redirection)**: البرنامج بيفضل يشتغل بمساراته الثابتة (`C:\Program Files\...` و`%LOCALAPPDATA%\...`)، لكن المسارات دي بقت **Symlink/Junction** بيوجّه للفولدر البورتابل. فالملفات بتتكتب وتتقرأ من فولدر الباكدج بدون ما البرنامج يعرف.

### الترتيب النموذجي

```text
1. خد مسار الـ EXE نفسه (AppDomain.CurrentDomain.BaseDirectory)

2. اعمل Symlink/Junction من كل مسار حقيقي لفولدر جوه <App>Data:
   %LOCALAPPDATA%\App        -> <EXE>\AppData\AppData\Local
   %APPDATA%\App             -> <EXE>\AppData\AppData\Roaming
   %ProgramData%\App         -> <EXE>\AppData\ProgramData
   C:\Program Files\App      -> <EXE>\AppData\ProgramFiles
   C:\Program Files (x86)\App-> <EXE>\AppData\ProgramFiles(x86)

3. استورد الريجستري:
   reg import "<EXE>\AppData\Regs\App.reg"

4. اعمل Windows Service (لو فيه) وشغّله:
   sc create ... ; sc start ...

5. استنى DelayMs ميلي ثانية

6. شغّل البرنامج من المسار الحقيقي (اللي هو Symlink للباكدج)
```

## 2) الملفات

```
PortableLauncher\
  Program.cs          ← الكود (C# 5، .NET Framework 4.8)
  launcher.ini        ← كل الإعدادات (مثال عام جاهز)
  EmbeddedConfig.cs   ← قالب التكوين المدمج Base64
  build.cmd           ← يبني PortableLauncher.exe
  README.md
```

### المميزات

- **Junction افتراضيًا** (`mklink /J`) — مش محتاج Admin لمجلدات المستخدم.
- **مش بيمسح بيانات**: لو المسار الحقيقي موجود، بيغيّر اسمه لـ `....portable-backup-YYYYMMDD-HHMMSS` بدل الحذف.
- بيرجّع الفولدرات الأصلية تلقائيًا لو `RestoreOnExit=1` + `WaitForExit=1`.
- استيراد `.reg`، إنشاء/تشغيل Service، وتشغيل برامج — كلها silent.
- `RequireAdmin=1` يعمل Self-elevation (UAC) لو محتاج.
- مفيش كونسول خالص (`/target:winexe`)، وكل حاجة بتتسجل في `launcher.log`.

### البناء

```bat
cd src\PortableLauncher
build.cmd
```

بيستخدم `csc.exe` الموجود مع .NET Framework — مش محتاج Visual Studio ولا .NET SDK.

### التشغيل

```bat
PortableLauncher.exe
```

أو صامت بالكامل:

```bat
PortableLauncher.exe --silent --config "D:\Apps\MyApp\launcher.ini"
```

### شكل `launcher.ini`

```ini
[App]
Name=My App Portable
LinkType=Symlink        ; Symlink = محتاج Admin | Junction = مش محتاج Admin
RequireAdmin=1           ; مطلوب لـ HKLM + Services + Program Files
Silent=1
LogFile=launcher.log
WaitForExit=0
RestoreOnExit=0
DelayMs=2000
; WaitProcess=MyAppEngine   ; لو الـ exe الأساسي launcher بيقفل بسرعة
WaitProcessGraceMs=15000

[Folders]
; المسار الحقيقي = الفولدر البورتابل (نسبي لمكان الـ EXE)
%LOCALAPPDATA%\MyApp=MyAppData\AppData\Local
%APPDATA%\MyApp=MyAppData\AppData\Roaming
%ProgramData%\MyApp=MyAppData\ProgramData
%ProgramFiles%\MyApp=MyAppData\ProgramFiles
%ProgramFiles(x86)%\MyApp=MyAppData\ProgramFiles(x86)

[CopyFolders]
; نفس الفكرة لكن بالنسخ بدل الـ Junction (يشتغل على FAT32/exFAT)
; %LOCALAPPDATA%\MyApp=Data\Copy\AppDataLocal\MyApp

[Overlay]
; ملفات مفردة تتنقل من/إلى جذر حقيقي
; %LOCALAPPDATA%=Data\Overlay\AppDataLocal

[Registry]
main=MyAppData\Regs\MyApp.reg

[RegistryExport]
; مفتاح = ملف .reg يتصدّر عند الخروج (لحفظ الإعدادات جوه الباكدج)
; HKCU\Software\MyApp=Regs\export_0.reg

[Services]
; الاسم = binPath | DisplayName | Description
MyAppService="C:\Program Files\MyApp\MyAppService.exe" --run-as-service|My App Service|My App background service.

[Run]
; مسار البرنامج = Arguments
C:\Program Files\MyApp\MyApp.exe=
```

### الوضعيات الأربعة

| القسم | بيعمل إيه | محتاج Admin؟ | بيشتغل على FAT32؟ |
|---|---|---|---|
| `[Folders]` | Junction: البرنامج يكتب في مساره الطبيعي والملفات تروح الباكدج | لمجلدات المستخدم: لا | لا |
| `[CopyFolders]` | نسخ مجلد كامل للباكدج/منه | حسب المسار | أيوه |
| `[Overlay]` | نسخ ملفات مفردة مع backup/restore | حسب المسار | أيوه |
| `[Registry]` + `[RegistryExport]` | import عند البدء و export عند الخروج | لـ HKLM: أيوه | أيوه |

- `RestoreOnExit=1` معناه: بعد ما البرنامج يقفل، الـ Junction بيتشال، الملفات اللي اتنسخت بتترجع/تتشال، وأي تغييرات اتحفظت في الباكدج الأول. محتاج `WaitForExit=1`.
- لو البرنامج الرئيسي بيبدأ من launcher صغير ويقفل فورًا، استخدم `WaitProcess` (مثلًا `WaitProcess=MyAppEngine`) عشان التنظيف ما يحصلش والبرنامج لسه شغال.
- `CopyFolders` بيحفظ أي ملفات جديدة عملها البرنامج (بعكس `Overlay`) لأنه بيعمل mirror للمجلد كله عند الخروج.

### ملاحظات مهمة

- **Backup بدل حذف**: اللانشر بيغيّر اسم أي فولدر حقيقي موجود لـ `....portable-backup-*` بدل ما يمسحه، فبياناتك مش بتضيع.
- لو برنامجك بيكتب في `%APPDATA%` (Roaming) لازم تضيفه في `[Folders]` يدويًا.
- السيرفس والريجستري **نظاميين/global** — مش بورتابل 100%. بعد الاستخدام بيفضلوا متسجلين على الجهاز.
- إنشاء Symlink محتاج Admin (أو Developer Mode). الـ Junction مش محتاج Admin لكن لازم NTFS وهدف محلي.

### إزاي تستخدمه مع أي برنامج تاني؟

أسهل طريقة: استخدم **PortableBuilder** (الفولدر اللي فيه `PortableBuilder.exe`). هو بيعمل Capture/Diff أوتوماتيك ويطلعلك الباكدج و`launcher.ini` جاهزين.

يدويًا:

1. ثبّت البرنامج على جهاز نضيف (VM أحسن).
2. شغّل **Process Monitor** وفلتر `Operation is CreateFile` + `Path contains اسم البرنامج`، وشوف بيكتب فين:
   - `%LOCALAPPDATA%` / `%APPDATA%` / `%ProgramData%` / `Program Files`
   - الريجستري (`RegOpenKey`).
3. انقل الفولدرات دي جوه `<App>Data` وحطها في `[Folders]`.
4. صدّر مفاتيح الريجستري اللي البرنامج محتاجها لـ `.reg` وحطها في `[Registry]`.
5. لو فيه Service، خده من `sc qc <name>` وحطه في `[Services]`.
6. حط أمر التشغيل في `[Run]`.

### نصائح "صامت" حقيقي

- عشان UAC ميظهرش كل مرة: اعمل **Task Scheduler** task بصلاحية `Highest privileges` وشغّله عند الـ logon. الـ Launcher نفسه `winexe` فمفيش نافذة.
- خلي `Silent=1` عشان الأخطاء تروح للّوج بس.
- لو عايز تشغيل بدون أي أثر: استخدم `RestoreOnExit=1` + `WaitForExit=1`، بس خلي بالك إن السيرفس هيفضل شغال بعد إغلاق البرنامج.

### حدود الـ "Portability"

- **Junction** لازم يكون الهدف على **NTFS محلي**. فلاشة FAT32/exFAT مش هتشتغل. الحل: `[CopyFolders]`، أو `LinkType=Symlink` مع Admin.
- الدرايفرات (`*.sys`) والخدمات والريجستري مش ممكن تبقى بورتابل 100% — بتتسجل على النظام.
- لو البرنامج بيستخدم مسارات مطلقة جوّه ملفات الإعداد، الـ Junction بيحلها، لكن لو بيسأل الـ Registry عن `InstallPath` لازم الـ `.reg` يكون مظبوط.
- `CopyFolders`/`Overlay` بينسخوا في كل تشغيل؛ للبرامج الكبيرة استخدم `[Folders]` على NTFS.
