; Inno Setup script for Reverse.Engineer.AI. Built by build-installer.ps1, which passes the /D defines used below.
; Per-user install (no admin prompt). The app, the bundled PrismML llama-server (ai\) and the AI model
; (ai\model\default.gguf, downloaded and SHA-256-verified by Setup) all live in the install folder. Settings and analysis
; projects stay in %LocalAppData%\ReaWin and are never touched by install, update or uninstall.
;
; The AI download logic is the shared installer kit (ai-tooling/windows/llm-installer), included below from {#KitDir}.
; English only: the model and the app's prompts are English.
; Command line: Setup.exe /MODEL=C:\path\Ternary-Bonsai-2-27B-PTQ1_0.gguf installs offline.

#ifndef Version
  #define Version "0.0.0"
#endif

[Setup]
AppId={{3D7A91C4-6E2B-4B58-A0F3-7C15E9D2B846}
AppName=Reverse.Engineer.AI
AppVersion={#Version}
AppPublisher=Ai.Slop.AI
DefaultDirName={autopf}\Reverse.Engineer.AI
DefaultGroupName=Reverse.Engineer.AI
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutDir}
OutputBaseFilename=ReverseEngineerAI-Setup-{#Version}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\ReaWin.exe
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no
InfoBeforeFile={#PrerequisitesFile}
#ifdef IconFile
SetupIconFile={#IconFile}
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; the published app, including its ai\ folder (llama-server and DLLs from Bundle-LlamaServer.ps1; the model is NOT here)
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Reverse.Engineer.AI"; Filename: "{app}\ReaWin.exe"
Name: "{autodesktop}\Reverse.Engineer.AI"; Filename: "{app}\ReaWin.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ReaWin.exe"; Description: "{cm:LaunchProgram,Reverse.Engineer.AI}"; Flags: nowait postinstall skipifsilent

; ---- the AI installer kit ------------------------------------------------------------------
#include AddBackslash(KitDir) + "inno\AiSlopLlm.messages.iss"
#include AddBackslash(KitDir) + "inno\AiSlopLlm.uninstall.iss"
#define AiSlopDefaultEvents
#include AddBackslash(KitDir) + "inno\AiSlopLlm.code.iss"
