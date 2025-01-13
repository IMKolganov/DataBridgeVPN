using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

const string TapctlPath = "tapctl.exe";
const string TapDeviceName = "Data Bridge VPN TAP Device";
const string VpnServerAddress = "192.168.1.1";
const int VpnServerPort = 51820;

Console.WriteLine("Запуск VPN-клиента...");

if (!File.Exists(TapctlPath))
{
    Console.WriteLine("Ошибка: tapctl.exe не найден рядом с приложением.");
    return;
}

string tapGuid = GetTapGuid();
if (string.IsNullOrEmpty(tapGuid))
{
    Console.WriteLine("TAP-устройство не найдено. Создаю новое устройство...");
    if (!CreateTapDevice(out string creationOutput))
    {
        Console.WriteLine($"Ошибка создания TAP-устройства: {creationOutput}");
        Console.WriteLine("Не удалось создать TAP-устройство. Завершение работы.");
        return;
    }
    tapGuid = creationOutput;
}

Console.WriteLine("TAP-устройство успешно настроено.");
Console.WriteLine("Начинаю обработку трафика...");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("Отмена операции пользователем...");
};

await ProcessTapTrafficAsync(tapGuid, cts.Token);

static bool TapDeviceExists()
{
    string output = ExecuteTapctlCommand("list");
    return output.Contains(TapDeviceName);
}

static bool CreateTapDevice(out string output)
{
    output = ExecuteTapctlCommand($"create --name \"{TapDeviceName}\"");
    if (output.Contains("failed") || !output.Contains("{"))
    {
        return false;
    }
    output = output.Split(new[] { '{', '}' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
    return true;
}

static string GetTapGuid()
{
    string output = ExecuteTapctlCommand("list");
    string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    foreach (string line in lines)
    {
        if (line.Contains(TapDeviceName))
        {
            int guidStart = line.IndexOf('{');
            int guidEnd = line.IndexOf('}');
            if (guidStart >= 0 && guidEnd > guidStart)
            {
                return line.Substring(guidStart + 1, guidEnd - guidStart - 1);
            }
        }
    }

    return null;
}
static async Task ProcessTapTrafficAsync(string tapGuid, CancellationToken cancellationToken)
{
    string tapDevicePath = $"\\\\.\\Global\\{{{tapGuid}}}.tap";
    Console.WriteLine($"Попытка открыть TAP-устройство по пути: {tapDevicePath}");

    try
    {
        using var fileStream = new FileStream(tapDevicePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Console.WriteLine("Обработка трафика TAP началась...");
        
        const int BufferSize = 1500;
        byte[] buffer = new byte[BufferSize];

        while (!cancellationToken.IsCancellationRequested)
        {
            int bytesRead = await fileStream.ReadAsync(buffer, 0, BufferSize, cancellationToken);
            Console.WriteLine($"Прочитано {bytesRead} байт из TAP-устройства.");
        }
    }
    catch (FileNotFoundException)
    {
        Console.WriteLine($"TAP-устройство не найдено по пути: {tapDevicePath}. Проверьте, что устройство создано.");
    }
    catch (UnauthorizedAccessException)
    {
        Console.WriteLine($"Нет прав для доступа к TAP-устройству. Запустите приложение от имени администратора.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка обработки трафика TAP: {ex.Message}");
    }
}


static string ExecuteTapctlCommand(string arguments)
{
    try
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = TapctlPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return output;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка выполнения команды tapctl: {ex.Message}");
        return string.Empty;
    }
}
