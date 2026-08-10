using System;
using System.Globalization;
using System.IO;

namespace Padlume
{
    /// <summary>
    /// Every user-facing piece of text goes through here — decided once, when the app starts. No
    /// localization framework (.resx) on purpose: without Visual Studio's designer to generate the
    /// strongly-typed code, hand-keeping two .resx files in sync would be more fragile than this — each
    /// string here already shows both versions side by side, so a mismatch jumps out during review.
    /// Internal-only text (App.Log, comments) is left out — only what actually appears on screen.
    /// </summary>
    public static class Strings
    {
        /// <summary>Language file written by installer/Padlume.iss based on which language the user
        /// picked on Setup's language page — the language a person deliberately chose during install
        /// should stick, regardless of whatever Windows' own display language happens to be.</summary>
        private static readonly string LanguageFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Padlume", "language.txt");

        /// <summary>True when the app should show English — decided once, when the class loads (before
        /// any window opens). Changing this while the app is already open does not switch the UI live
        /// (unlike the theme); Padlume needs to be reopened. Prefers the language picked during
        /// installation (see LanguageFilePath); falls back to the Windows display language (Settings >
        /// Time & language > Language) for anyone who didn't go through that installer.</summary>
        public static readonly bool IsEnglish = DetermineIsEnglish();

        private static bool DetermineIsEnglish()
        {
            try
            {
                if (File.Exists(LanguageFilePath))
                {
                    var content = File.ReadAllText(LanguageFilePath).Trim();
                    if (content.Equals("en", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (content.Equals("pt", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
            catch
            {
                // Falls through to the OS-culture default below.
            }

            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase);
        }

        private static string T(string pt, string en) => IsEnglish ? en : pt;

        // Header / controller selection
        public static string ControllersDetected => T("Controles detectados", "Controllers detected");
        public static string BluetoothOrUsb => T("Bluetooth ou USB", "Bluetooth or USB");
        public static string SelectController => T("Selecionar controle", "Select controller");
        public static string RefreshTooltip => T("Atualizar", "Refresh");
        public static string CompactModeTooltip => T("Modo compacto", "Compact mode");
        public static string ChooseTheController => T("Escolha o controle", "Choose a controller");
        public static string NoControllerConnected => T("Nenhum controle conectado.", "No controller connected.");
        public static string Blocked => T("🔒 Bloqueado — clique para priorizar", "🔒 Blocked — click to prioritize");

        // Selected controller card
        public static string NoControllerSelected => T("Nenhum controle selecionado", "No controller selected");
        public static string NotConnected => T("Não conectado", "Not connected");
        public static string Connected => T("Conectado", "Connected");
        public static string IdPlaceholder => T("ID: --", "ID: --");
        public static string GoodBattery => T("Bateria boa", "Good battery");
        public static string MediumBattery => T("Bateria média", "Medium battery");
        public static string BatteryLow => T("Bateria baixa", "Low battery");
        public static string NotAvailable => T("N/D", "N/A");

        public static string ConnectedPercent(double pct) => T($"Conectado • {pct:0}%", $"Connected • {pct:0}%");

        // Status/info panel
        public static string NoControllerFoundTitle => T("Nenhum controle encontrado.", "No controller found.");
        public static string PairControllerSubtitle => T("Pareie o controle em Configurações > Bluetooth e dispositivos.", "Pair the controller in Settings > Bluetooth & devices.");
        public static string SelectControllerTitle => T("Selecione um controle na lista.", "Select a controller from the list.");
        public static string UseButtonSubtitle => T($"Use o botão \"{SelectController}\" no topo para escolher.", $"Use the \"{SelectController}\" button at the top to choose.");
        public static string BatteryReadErrorTitle => T("Erro ao ler bateria.", "Error reading battery.");
        public static string CheckingBluetoothTitle => T("Consultando bateria via Bluetooth...", "Checking battery over Bluetooth...");
        public static string MayTakeAFewSecondsSubtitle => T("Isso pode levar alguns segundos.", "This can take a few seconds.");
        public static string ControllerDoesNotReportBatteryTitle => T("Este controle não reporta bateria.", "This controller doesn't report battery.");
        public static string SomeControllersDontReportSubtitle => T("Alguns controles não informam o nível de bateria ao Windows.", "Some controllers don't report battery level to Windows.");
        public static string ExactLevelUnavailableSubtitle => T("O nível exato de bateria não está disponível para este controle.", "The exact battery level isn't available for this controller.");
        public static string ControllerConnectedWorkingTitle => T("Controle conectado e funcionando corretamente.", "Controller connected and working correctly.");
        public static string CouldNotBlockTitle => T("Não foi possível bloquear os outros controles.", "Couldn't block the other controllers.");
        public static string RunAsAdminSubtitle => T("Execute o Padlume como Administrador para impor a prioridade de controle.", "Run Padlume as Administrator to enforce controller priority.");
        public static string CouldNotChangeStartupTitle => T("Não foi possível alterar a inicialização automática.", "Couldn't change automatic startup.");
        public static string WindowsDeniedChangeSubtitle => T("O Windows negou a alteração no registro (pode estar bloqueado por política do sistema).", "Windows denied the registry change (may be blocked by system policy).");

        public static string BatteryStatusText(string status) => T($"Status da bateria: {status}", $"Battery status: {status}");
        public static string ReconnectIfNotUpdating(string statusText) => T(
            $"Status: {statusText}. Se a porcentagem não estiver atualizando, tente reconectar o controle.",
            $"Status: {statusText}. If the percentage isn't updating, try reconnecting the controller.");
        public static string IfNotUpdatingReconnect => T("Se a porcentagem não estiver atualizando, tente reconectar o controle.", "If the percentage isn't updating, try reconnecting the controller.");

        // History
        public static string BatteryHistoryLabel => T("Histórico da bateria", "Battery history");
        public static string EstimatedTimeRemaining => T("Tempo estimado restante", "Estimated time remaining");
        public static string AverageUsage => T("Consumo médio", "Average usage");
        public static string SelectToViewHistory => T("Selecione um controle para ver o histórico.", "Select a controller to see its history.");
        public static string NotEnoughDataYet => T("Ainda não há dados suficientes. O histórico é construído automaticamente enquanto o app fica aberto.", "Not enough data yet. History builds up automatically while the app stays open.");
        public static string Charging => T("Carregando", "Charging");
        public static string Discharging => T("Descarregando", "Discharging");
        public static string ConnectedIdle => T("Conectado (ocioso)", "Connected (idle)");
        public static string BatteryNotFound => T("Bateria não encontrada", "Battery not found");
        public static string CollectingData => T("Coletando dados...", "Collecting data...");

        public static string LastHours(double hours) => T($"Últimas {hours:0.#}h", $"Last {hours:0.#}h");
        public static string LastMinutes(double minutes) => T($"Últimos {minutes:0}min", $"Last {minutes:0}min");
        public static string PercentPerHour(double rate) => T($"{rate:0.0}%/hora", $"{rate:0.0}%/hour");
        public static string MinMaxReadings(int min, int max, int count) => T($"Mín {min}% • Máx {max}% • {count} leituras", $"Min {min}% • Max {max}% • {count} readings");
        public static string RemainingLong(int hours, int minutes) => T($"~{hours}h {minutes}min restantes", $"~{hours}h {minutes}min remaining");
        public static string RemainingShort(int minutes) => T($"~{minutes}min restantes", $"~{minutes}min remaining");

        // Tips
        public static string Tips => T("Dicas", "Tips");
        public static string TipControllerOn => T("• Certifique-se de que o controle esteja ligado.", "• Make sure the controller is turned on.");
        public static string TipBluetoothProximity => T("• Se estiver usando Bluetooth, mantenha o controle próximo ao computador.", "• If using Bluetooth, keep the controller close to the computer.");
        public static string TipBluetoothMayNotReport => T("• Alguns controles podem não informar a bateria corretamente via Bluetooth.", "• Some controllers may not report battery correctly over Bluetooth.");
        public static string TipHistoryUpdates => T("• O histórico de bateria é atualizado a cada 5 minutos.", "• Battery history updates every 5 minutes.");

        // Settings
        public static string StartWithWindows => T("Iniciar automaticamente com o Windows", "Start automatically with Windows");
        public static string AllowPhoneControl => T("Permitir controlar pelo celular", "Allow phone control");
        public static string PhoneControlHint(string url) => T(
            $"Acesse http://{url} pelo navegador do celular, na mesma rede Wi-Fi.",
            $"Open http://{url} in your phone's browser, on the same Wi-Fi network.");
        public static string PhoneControlUnavailable => T("Não foi possível iniciar o servidor local.", "Couldn't start the local server.");
        public static string PhoneControlNoNetwork => T("Nenhuma rede Wi-Fi/local detectada.", "No local network detected.");

        // Tray / flyout
        public static string OpenApp => T("Abrir aplicativo", "Open app");
        public static string SettingsLabel => T("Configurações", "Settings");
        public static string Exit => T("Sair", "Exit");

        // Compact widget
        public static string BackToNormalWindow => T("Voltar pra janela normal", "Back to normal window");

        // Low battery warning
        public static string LowBatteryToastTitle => T("Bateria fraca", "Low battery");

        // Startup
        public static string AlreadyRunning => T("O Padlume já está em execução.", "Padlume is already running.");

        // Update available
        public static string UpdateAvailableTitle(string version) => T($"Atualização disponível: {version}", $"Update available: {version}");
        public static string UpdateAvailableSubtitle => T("Baixa e instala a versão nova automaticamente — o Padlume fecha e reabre sozinho.", "Downloads and installs the new version automatically — Padlume closes and reopens on its own.");
        public static string UpdateNow => T("Atualizar agora", "Update now");
        public static string UpdateLater => T("Mais tarde", "Later");
        public static string Downloading => T("Baixando atualização...", "Downloading update...");
        public static string UpdateVerifying => T("Conferindo integridade do arquivo...", "Verifying file integrity...");
        public static string UpdateFailed => T("Não foi possível baixar a atualização. Tente novamente mais tarde.", "Couldn't download the update. Try again later.");
        public static string UpdateChecksumMismatch => T("O arquivo baixado não bateu com o esperado — atualização cancelada por segurança.", "The downloaded file didn't match what was expected — update canceled for safety.");
    }
}
