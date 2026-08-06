; Installer for JBU CodeLens.
;
; Build with:
;   ISCC.exe packaging\JBU.CodeLens.iss /DSourceDir="<published folder>"
;
; The published folder is produced by:
;   dotnet publish src\JBU.CodeLens.UI\JBU.CodeLens.UI.csproj -c Release -r win-x64 ^
;       --self-contained true -o <folder>
; and must have the .gguf model copied into its models\ subfolder before packaging.

#ifndef SourceDir
  #error Pass the published folder with /DSourceDir="..."
#endif

; Read the version straight off the built executable so the installer can never
; disagree with what it is installing.
#define AppExe SourceDir + "\JBU.CodeLens.UI.exe"
#define AppVersion GetVersionNumbersString(AppExe)

[Setup]
; Never reuse this GUID for another product: it is how Windows recognises an
; upgrade of this app rather than a second copy of it.
AppId={{8F3A6C21-5D74-4E93-9A18-2C7B41E6D0F5}
AppName=JBU CodeLens
AppVersion={#AppVersion}
AppVerName=JBU CodeLens {#AppVersion}
AppPublisher=Burair and Usaid
VersionInfoVersion={#AppVersion}

; Per-user install: no administrator prompt, and it works when the person
; running it is not an administrator on their own machine — which is the common
; case on university and work laptops.
PrivilegesRequired=lowest
DefaultDirName={autopf}\JBU CodeLens
DefaultGroupName=JBU CodeLens
DisableProgramGroupPage=yes
AllowNoIcons=yes

; The app is a 64-bit build and will not run anywhere else.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Relative paths resolve against this script's folder, so the icon is found
; without depending on where the published output happens to sit.
#ifndef OutputDir
  #define OutputDir "..\dist-installer"
#endif
OutputDir={#OutputDir}
OutputBaseFilename=JBU-CodeLens-Setup
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\JBU.CodeLens.UI.exe
WizardStyle=modern

; The language model is roughly a gigabyte of already-quantised weights, which
; is close to incompressible. It is excluded from compression below, so max
; settings here cost little time and only apply to the parts that do compress.
Compression=lzma2/max
SolidCompression=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; The model: stored without compression. Trying to compress quantised weights
; costs several minutes of build time and saves almost nothing.
Source: "{#SourceDir}\models\*"; DestDir: "{app}\models"; Flags: ignoreversion nocompression

; Everything else, including the native libclang and llama libraries under
; runtimes\win-x64. recursesubdirs is required: the app fails at runtime with a
; DllNotFoundException if those are missing.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "models\*"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\JBU CodeLens"; Filename: "{app}\JBU.CodeLens.UI.exe"
Name: "{group}\Uninstall JBU CodeLens"; Filename: "{uninstallexe}"
Name: "{autodesktop}\JBU CodeLens"; Filename: "{app}\JBU.CodeLens.UI.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\JBU.CodeLens.UI.exe"; Description: "Launch JBU CodeLens"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Uninstall removes the program but deliberately leaves %APPDATA%\JBU.CodeLens
; alone: it holds the person's settings and saved AI explanations, and a
; reinstall should not start from nothing. Removing the app folder itself is
; enough, and this clears anything the app wrote inside it.
Type: filesandordirs; Name: "{app}\models"
