namespace Ansight.Cli.Commands.Doctor;

internal static class DoctorCheckGuidance
{
    public static string GetJustification(string checkName)
        => checkName switch
        {
            "android.sdk" or "android.connection" => "ADB connects this host to the Android target; local emulator dependencies are not needed for external targets.",
            "android.emulator" or "android.system-image" or "android.acceleration" or "android.kvm" => "A local Android emulator requires SDK components, a configured system image, and accessible hardware acceleration.",
            "data-directory" =>
                "Ansight stores host state, sessions, captures, Trends history, and local configuration in this directory.",
            "credential-vault" =>
                "Ansight uses protected credential storage for account credentials and test secrets.",
            "secret-store" or "secret-store-access" =>
                "The encrypted secret store protects credentials and test secrets when Ansight uses file-backed storage.",
            "secret-master-key" =>
                "The master key encrypts the file-backed secret store without keeping its protection key beside the data.",
            "tool.node" =>
                "Node.js runs trusted workspace tasks, triggers, and custom sanitizers written in JavaScript or TypeScript.",
            "native.skia" =>
                "SkiaSharp provides screenshot evidence, image comparison, and screenshot-backed UI actions.",
            "tool.tesseract" =>
                "Tesseract detects text locally for screenshot OCR and PII redaction.",
            "tool.scrcpy" =>
                "scrcpy and its matching server provide Android video and display streaming.",
            "tool.dotnet-trace" =>
                "dotnet-trace captures diagnostic traces for Ansight's beta .NET profiling workflows.",
            "tool.dotnet-dsrouter" =>
                "dotnet-dsrouter routes diagnostics for supported device workflows in Ansight's beta .NET profiler.",
            "tool.appium" =>
                "The Appium CLI supports UI input on physical iOS devices and manages the required XCUITest driver.",
            "device.ios.appium.xcuitest" =>
                "The Appium XCUITest driver translates Ansight input actions into automation on physical iOS devices.",
            "device.ios.appium.server" =>
                "A running Appium server provides the WebDriver session used for physical-iOS input.",
            "device.android" =>
                "Android SDK tools provide device discovery, app lifecycle, logs, screenshots, location, input, emulators, and beta Perfetto profiling.",
            "device.ios" =>
                "Full Xcode provides iOS Simulator discovery and lifecycle through simctl, physical-device lifecycle through devicectl, and beta Instruments profiling.",
            "device.ios.simulator-hid" =>
                "The SimulatorKit HID bridge sends real taps, swipes, text, and button input to iOS Simulators.",
            "permission.accessibility" =>
                "The Ansight process needs Accessibility access to verify Simulator's selected audio input before recording or injection.",
            "device.ios.audio.blackhole" =>
                "The optional BlackHole loopback driver routes an audio fixture into the iOS Simulator microphone without changing the Mac's default audio devices.",
            _ =>
                "This check reports whether the local machine supports an Ansight host capability."
        };

    public static string GetInstallInstructions(string checkName)
        => checkName switch
        {
            "android.sdk" => "Install Android SDK Platform-Tools and set ANDROID_HOME or ANSIGHT_ADB_PATH.",
            "android.connection" => "Connect and authorize the device in adb devices; for a remote emulator configure ADB connectivity.",
            "android.emulator" or "android.system-image" => "Install Android SDK command-line tools, emulator, and a compatible system image with sdkmanager; create an AVD with avdmanager.",
            "android.acceleration" or "android.kvm" => "Enable hardware/nested virtualization and grant the host user KVM access on Linux, then run emulator -accel-check. Otherwise use --target android-device with an external target.",
            "data-directory" =>
                "Create the directory if needed and grant the user running Ansight permission to read and write it.",
            "credential-vault" => GetCredentialVaultInstructions(),
            "secret-store" or "secret-store-access" =>
                "No separate install is needed; configure a protected master key and run an Ansight auth or secret command to create or migrate the store.",
            "secret-master-key" =>
                "Set ANSIGHT_SECRET_MASTER_KEY, configure --secret-key-file, or supply the ansight-secret-master-key systemd credential with owner-only permissions.",
            "tool.node" =>
                "Install a Node.js LTS release and make the node executable available on PATH.",
            "native.skia" =>
                "Reinstall the Ansight CLI build for this operating system and CPU; the supported SkiaSharp native runtime is bundled.",
            "tool.tesseract" => GetTesseractInstructions(),
            "tool.scrcpy" => GetScrcpyInstructions(),
            "tool.dotnet-trace" =>
                "Run 'dotnet tool install --global dotnet-trace' and add the .NET global-tools directory to PATH.",
            "tool.dotnet-dsrouter" =>
                "Run 'dotnet tool install --global dotnet-dsrouter' and add the .NET global-tools directory to PATH.",
            "tool.appium" =>
                "Install Node.js, then run 'npm install --global appium' and make the appium executable available on PATH.",
            "device.ios.appium.xcuitest" =>
                "Install Appium, then run 'appium driver install xcuitest'.",
            "device.ios.appium.server" =>
                "Install Appium and run 'appium' in a persistent terminal; set ANSIGHT_APPIUM_SERVER_URL if it does not use the default address.",
            "device.android" =>
                "Install Android Studio, then install Android SDK Platform-Tools and Android Emulator from SDK Manager; set ANSIGHT_ADB_PATH if needed.",
            "device.ios" =>
                "Install full Xcode, launch it once, then select its Developer directory with xcode-select or ANSIGHT_XCODE_PATH.",
            "device.ios.simulator-hid" =>
                "Install and select a compatible full Xcode build, then reinstall the matching Ansight CLI if the bundled bridge cannot load.",
            "permission.accessibility" =>
                "Open System Settings > Privacy & Security > Accessibility with: open 'x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility'. Enable the application that launches the Ansight host (Terminal, iTerm, or your IDE); use + to add it if missing. Run doctor from that same launcher. If access is still denied, restart the host from that launcher. macOS remembers grants, but a different launcher or changed code-signing identity may need approval again. This check does not grant access or verify Simulator's audio route.",
            "device.ios.audio.blackhole" =>
                "Run 'brew install --cask blackhole-2ch' and complete the installer's administrator and restart prompts. Restart simulators that were already booted when the driver was installed or CoreAudio was restarted. Select BlackHole 2ch under Simulator > I/O > Audio Input (Sound Input on some Xcode versions). Allow Accessibility for the application running Ansight so it can verify the selection. Keep the Mac's system audio defaults unchanged. ANSIGHT_AUDIO_DEVICE_UID can select another explicitly configured duplex loopback device.",
            _ =>
                "Review the check message and the Ansight dependency guide for the required setup."
        };

    private static string GetCredentialVaultInstructions()
    {
        if (OperatingSystem.IsLinux())
        {
            return "Install and start a Secret Service provider, or configure Ansight's protected file store with an externally managed master key.";
        }

        return "No separate install is normally needed; Ansight uses macOS Keychain or Windows DPAPI supplied by the operating system.";
    }

    private static string GetTesseractInstructions()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            return "Run 'brew install tesseract', or set ANSIGHT_TESSERACT_PATH to an existing executable.";
        }

        if (OperatingSystem.IsWindows())
        {
            return "Run 'winget install UB-Mannheim.TesseractOCR', then add it to PATH or set ANSIGHT_TESSERACT_PATH.";
        }

        return "Install the tesseract-ocr package with the system package manager, or set ANSIGHT_TESSERACT_PATH.";
    }

    private static string GetScrcpyInstructions()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            return "Run 'brew install scrcpy' so the executable and matching scrcpy-server are installed together.";
        }

        if (OperatingSystem.IsWindows())
        {
            return "Run 'winget install --exact Genymobile.scrcpy' and make scrcpy available on PATH.";
        }

        return "Install a complete scrcpy package for the system so the executable and matching scrcpy-server are installed together.";
    }
}
