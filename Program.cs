using System;
using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

const string TAP_DEVICE_PATH = "\\.\\Global\\{YOUR_TAP_GUID}.tap"; // Укажите GUID вашего TAP-устройства
const int BUFFER_SIZE = 1500; // Размер Ethernet-фрейма

[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
static extern SafeFileHandle CreateFile(
    string lpFileName,
    uint dwDesiredAccess,
    uint dwShareMode,
    IntPtr lpSecurityAttributes,
    uint dwCreationDisposition,
    uint dwFlagsAndAttributes,
    IntPtr hTemplateFile
);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool DeviceIoControl(
    SafeFileHandle hDevice,
    uint dwIoControlCode,
    byte[] lpInBuffer,
    uint nInBufferSize,
    byte[] lpOutBuffer,
    uint nOutBufferSize,
    ref uint lpBytesReturned,
    IntPtr lpOverlapped
);

const uint GENERIC_READ_WRITE = 0xC0000000;
const uint OPEN_EXISTING = 3;
const uint TAP_IOCTL_SET_MEDIA_STATUS = 0x22E014;

Console.WriteLine("VPN-клиент запущен...");

SafeFileHandle tapHandle = CreateFile(
    TAP_DEVICE_PATH,
    GENERIC_READ_WRITE,
    0,
    IntPtr.Zero,
    OPEN_EXISTING,
    0,
    IntPtr.Zero
);

if (tapHandle.IsInvalid)
{
    int errorCode = Marshal.GetLastWin32Error();
    Console.WriteLine($"Не удалось открыть TAP-устройство. Код ошибки: {errorCode}");

    // Расшифровка ошибок
    switch (errorCode)
    {
        case 2:
            Console.WriteLine("Ошибка 2: Устройство не найдено. Проверьте, установлен ли TAP-драйвер и правильность пути.");
            break;
        case 3:
            Console.WriteLine("Ошибка 3: Путь не существует. Убедитесь, что TAP-устройство доступно и GUID указан верно.");
            break;
        case 5:
            Console.WriteLine("Ошибка 5: Доступ запрещён. Запустите приложение от имени администратора.");
            break;
        default:
            Console.WriteLine("Неизвестная ошибка. Проверьте конфигурацию системы.");
            break;
    }

    return;
}

Console.WriteLine("TAP-устройство успешно открыто.");

// Активируем TAP-устройство
ActivateTapDevice(tapHandle);

// Запускаем обработку трафика
await ProcessTapTrafficAsync(tapHandle);

static void ActivateTapDevice(SafeFileHandle tapHandle)
{
    byte[] statusBuffer = BitConverter.GetBytes(1);
    uint bytesReturned = 0;

    bool success = DeviceIoControl(
        tapHandle,
        TAP_IOCTL_SET_MEDIA_STATUS,
        statusBuffer,
        (uint)statusBuffer.Length,
        null,
        0,
        ref bytesReturned,
        IntPtr.Zero
    );

    if (success)
    {
        Console.WriteLine("TAP-устройство активировано.");
    }
    else
    {
        Console.WriteLine("Не удалось активировать TAP-устройство.");
    }
}

static async Task ProcessTapTrafficAsync(SafeFileHandle tapHandle)
{
    byte[] buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);

    try
    {
        using var stream = new FileStream(tapHandle, FileAccess.ReadWrite, BUFFER_SIZE, isAsync: true);
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
