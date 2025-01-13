using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

const int BUFFER_SIZE = 1500; // Размер Ethernet-фрейма

Console.WriteLine("Запуск VPN-клиента...");

string devconPath = GetDevconPath();
if (devconPath == null)
{
    Console.WriteLine("Не найден инструмент devcon.exe. Завершение работы.");
    return;
}

Console.WriteLine($"Путь к devcon.exe: {devconPath}");

if (!IsTapDriverInstalled())
{
    Console.WriteLine("TAP-драйвер не установлен. Попытка установить...");
    InstallTapDriver();
    if (!IsTapDriverInstalled())
    {
        Console.WriteLine("Не удалось установить TAP-драйвер. Завершение работы.");
        return;
    }
    else
    {
        Console.WriteLine("TAP-драйвер успешно установлен.");
    }
}

string tapDevicePath = GetTapDevicePath();
if (tapDevicePath == null)
{
    Console.WriteLine("Не удалось найти TAP-устройство.");
    return;
}

Console.WriteLine($"TAP-устройство найдено: {tapDevicePath}");

var tapHandle = OpenTapDevice(tapDevicePath);
if (tapHandle == IntPtr.Zero)
{
    Console.WriteLine("Не удалось открыть TAP-устройство.");
    return;
}

Console.WriteLine("TAP-устройство успешно открыто.");

// Запуск обработки трафика
await ProcessTapTrafficAsync(tapHandle);

Console.WriteLine("Завершение работы VPN-клиента.");

static bool IsTapDriverInstalled()
{
    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "devcon.exe",
            Arguments = "find tap0901",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };

    process.Start();
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();

    return output.Contains("TAP-Windows Adapter V9"); // Проверяем по имени адаптера
}

static void InstallTapDriver()
{
    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c addtap.bat",
            WorkingDirectory = @"C:\Program Files\TAP-Windows\bin",
            UseShellExecute = true,
            Verb = "runas" // Запуск от имени администратора
        }
    };

    process.Start();
    process.WaitForExit();

    if (process.ExitCode == 0)
    {
        Console.WriteLine("TAP-драйвер успешно установлен.");
    }
    else
    {
        Console.WriteLine($"Ошибка установки TAP-драйвера. Код: {process.ExitCode}");
    }
}

static string GetTapDevicePath()
{
    string query = "SELECT * FROM Win32_NetworkAdapter WHERE Name LIKE '%TAP-Windows Adapter V9%'";
    using (var searcher = new ManagementObjectSearcher(query))
    {
        foreach (ManagementObject obj in searcher.Get())
        {
            string pnpDeviceId = obj["PNPDeviceID"]?.ToString();
            if (pnpDeviceId != null)
            {
                // PNPDeviceID имеет формат "ROOT\\TAP0901\\{GUID}"
                int startIndex = pnpDeviceId.IndexOf('{');
                int endIndex = pnpDeviceId.IndexOf('}');
                if (startIndex != -1 && endIndex != -1)
                {
                    string guid = pnpDeviceId.Substring(startIndex, endIndex - startIndex + 1);
                    return $"\\\\.\\Global\\{guid}.tap"; // Формируем путь к устройству
                }
            }
        }
    }
    return null; // Устройство не найдено
}

static IntPtr OpenTapDevice(string tapDevicePath)
{
    const uint GENERIC_READ_WRITE = 0xC0000000;
    const uint OPEN_EXISTING = 3;

    IntPtr handle = CreateFile(
        tapDevicePath,
        GENERIC_READ_WRITE,
        0,
        IntPtr.Zero,
        OPEN_EXISTING,
        0,
        IntPtr.Zero
    );

    if (handle == IntPtr.Zero)
    {
        int errorCode = Marshal.GetLastWin32Error();
        Console.WriteLine($"Не удалось открыть TAP-устройство. Код ошибки: {errorCode}");
    }

    return handle;
}

static async Task ProcessTapTrafficAsync(IntPtr tapHandle)
{
    byte[] buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);

    try
    {
        using var stream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(tapHandle, false), FileAccess.ReadWrite, BUFFER_SIZE, isAsync: true);
        using var udpClient = new UdpClient();
        udpClient.Connect("192.168.1.1", 51820); // Укажите адрес и порт вашего VPN-сервера

        Console.WriteLine("Обработка трафика TAP началась...");

        while (true)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, BUFFER_SIZE));
            if (bytesRead > 0)
            {
                Console.WriteLine($"Принято {bytesRead} байт из TAP.");

                // Отправка данных на VPN-сервер
                await udpClient.SendAsync(buffer.AsMemory(0, bytesRead));
                Console.WriteLine("Данные отправлены на сервер VPN.");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка обработки трафика: {ex.Message}");
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
}

static string GetTapToolsPath()
{
    const string registryKey = @"SOFTWARE\OpenVPN";
    const string valueName = "InstallDir";

    using var key = Registry.LocalMachine.OpenSubKey(registryKey);
    if (key != null)
    {
        string installPath = key.GetValue(valueName) as string;
        if (!string.IsNullOrEmpty(installPath))
        {
            string devconPath = Path.Combine(installPath, "bin", "devcon.exe");
            if (File.Exists(devconPath))
            {
                return devconPath;
            }
        }
    }

    return null; // devcon.exe не найден
}

static string GetDevconPath()
{
    // Массив возможных имён ключей в реестре
    string[] valueNames = { "InstallDir", "bin_dir" };
    const string registryKey = @"SOFTWARE\OpenVPN";

    // 1. Поиск рядом с приложением
    string localDevconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "devcon.exe");
    if (File.Exists(localDevconPath))
    {
        return localDevconPath;
    }

    // 2. Поиск в реестре
    using var key = Registry.LocalMachine.OpenSubKey(registryKey);
    if (key != null)
    {
        foreach (var valueName in valueNames)
        {
            string installPath = key.GetValue(valueName) as string;
            if (!string.IsNullOrEmpty(installPath))
            {
                string devconPath = Path.Combine(installPath, "bin", "devcon.exe");
                if (File.Exists(devconPath))
                {
                    return devconPath;
                }
            }
        }
    }

    Console.WriteLine("Не удалось найти devcon.exe. Убедитесь, что TAP-драйвер установлен.");
    return null;
}

[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
static extern IntPtr CreateFile(
    string lpFileName,
    uint dwDesiredAccess,
    uint dwShareMode,
    IntPtr lpSecurityAttributes,
    uint dwCreationDisposition,
    uint dwFlagsAndAttributes,
    IntPtr hTemplateFile
);
