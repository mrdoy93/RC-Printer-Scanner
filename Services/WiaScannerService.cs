using System.IO;
using System.Runtime.InteropServices;
using PrinterScanner.Models;

namespace PrinterScanner.Services;

public sealed class WiaScannerService
{
    private const int ScannerDeviceType = 1;
    private const int DocumentHandlingSelect = 3088;
    private const int Feeder = 1;
    private const int Flatbed = 2;
    private const int CurrentIntent = 6146;
    private const int XResolution = 6147;
    private const int YResolution = 6148;
    private const int XPosition = 6149;
    private const int YPosition = 6150;
    private const int XExtent = 6151;
    private const int YExtent = 6152;
    private const int IntentColor = 1;
    private const int IntentGrayscale = 2;
    private const int IntentText = 4;
    private const string PngFormat = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";

    public Task<IReadOnlyList<ScannerDevice>> GetScannersAsync() => RunSta(() =>
    {
        var scanners = new List<ScannerDevice>();
        dynamic? manager = null;

        try
        {
            manager = CreateDeviceManager();
            foreach (dynamic info in manager.DeviceInfos)
            {
                try
                {
                    if ((int)info.Type != ScannerDeviceType)
                    {
                        continue;
                    }

                    var id = Convert.ToString(info.DeviceID) ?? string.Empty;
                    var name = ReadProperty(info.Properties, 7, "Name") ?? "Scanner";
                    var manufacturer = ReadProperty(info.Properties, 3, "Manufacturer") ?? string.Empty;
                    scanners.Add(new ScannerDevice(id, name, manufacturer));
                }
                finally
                {
                    ReleaseCom(info);
                }
            }

            return (IReadOnlyList<ScannerDevice>)scanners
                .OrderByDescending(scanner => scanner.IsPreferredBrand)
                .ThenBy(scanner => scanner.Name)
                .ToList();
        }
        finally
        {
            ReleaseCom(manager);
        }
    });

    public Task<(bool SupportsA3, bool SupportsLegal)> GetLargePaperSupportAsync(
        ScannerDevice scanner) => RunSta(() =>
    {
        dynamic? manager = null;
        dynamic? selectedInfo = null;
        dynamic? device = null;
        dynamic? item = null;

        try
        {
            manager = CreateDeviceManager();
            foreach (dynamic info in manager.DeviceInfos)
            {
                if (string.Equals(Convert.ToString(info.DeviceID), scanner.Id, StringComparison.OrdinalIgnoreCase))
                {
                    selectedInfo = info;
                    break;
                }

                ReleaseCom(info);
            }

            if (selectedInfo is null)
            {
                return (false, false);
            }

            device = selectedInfo.Connect();
            TrySetProperty(device.Properties, DocumentHandlingSelect, Flatbed);
            item = device.Items[1];
            TrySetProperty(item.Properties, CurrentIntent, IntentColor);
            TrySetProperty(item.Properties, XResolution, 300);
            TrySetProperty(item.Properties, YResolution, 300);
            var xOriginApplied = TrySetProperty(item.Properties, XPosition, 0, requireExact: true);
            var yOriginApplied = TrySetProperty(item.Properties, YPosition, 0, requireExact: true);
            var originApplied = xOriginApplied && yOriginApplied;

            var actualXResolution = ReadIntProperty(item.Properties, XResolution) ?? 300;
            var actualYResolution = ReadIntProperty(item.Properties, YResolution) ?? 300;
            bool supportsLegal = originApplied && CanUseScanArea(
                item.Properties,
                "Legal",
                actualXResolution,
                actualYResolution);
            bool supportsA3 = originApplied && CanUseScanArea(
                item.Properties,
                "A3",
                actualXResolution,
                actualYResolution);
            return (supportsA3, supportsLegal);
        }
        catch
        {
            // If the driver cannot expose its scan area, keep large sizes hidden so
            // the user is not offered an option that will fail during acquisition.
            return (false, false);
        }
        finally
        {
            ReleaseCom(item);
            ReleaseCom(device);
            ReleaseCom(selectedInfo);
            ReleaseCom(manager);
        }
    });

    public Task ScanAsync(
        ScannerDevice scanner,
        ScanOptions options,
        string outputPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default) => RunSta(() =>
    {
        dynamic? manager = null;
        dynamic? selectedInfo = null;
        dynamic? device = null;
        dynamic? item = null;
        dynamic? imageFile = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(3);
            manager = CreateDeviceManager();
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(10);
            foreach (dynamic info in manager.DeviceInfos)
            {
                if (string.Equals(Convert.ToString(info.DeviceID), scanner.Id, StringComparison.OrdinalIgnoreCase))
                {
                    selectedInfo = info;
                    break;
                }

                ReleaseCom(info);
            }

            if (selectedInfo is null)
            {
                throw new InvalidOperationException("The selected scanner is no longer connected.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(20);
            device = selectedInfo.Connect();
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(30);
            TrySetProperty(device.Properties, DocumentHandlingSelect,
                options.Source == ScanSource.AutomaticDocumentFeeder ? Feeder : Flatbed);

            item = device.Items[1];
            TrySetProperty(item.Properties, CurrentIntent, IntentFor(options.ColorMode));
            TrySetProperty(item.Properties, XResolution, options.Dpi);
            TrySetProperty(item.Properties, YResolution, options.Dpi);
            var xOriginApplied = TrySetProperty(item.Properties, XPosition, 0, requireExact: true);
            var yOriginApplied = TrySetProperty(item.Properties, YPosition, 0, requireExact: true);
            var originApplied = xOriginApplied && yOriginApplied;

            var (widthInches, heightInches) = PaperDimensions(options.PaperSize);
            var actualXResolution = ReadIntProperty(item.Properties, XResolution) ?? options.Dpi;
            var actualYResolution = ReadIntProperty(item.Properties, YResolution) ?? options.Dpi;
            var widthApplied = TrySetExtent(
                item.Properties,
                XExtent,
                (int)Math.Round(widthInches * actualXResolution));
            var heightApplied = TrySetExtent(
                item.Properties,
                YExtent,
                (int)Math.Round(heightInches * actualYResolution));
            if (!originApplied || !widthApplied || !heightApplied)
            {
                var message = options.PaperSize is "A3" or "Legal"
                    ? "A3 or Legal is not available on this Printer Model"
                    : $"The scanner driver could not apply the full {options.PaperSize} scan area. " +
                      "Try a lower resolution or update the scanner driver.";
                throw new InvalidOperationException(message);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(40);
            imageFile = item.Transfer(PngFormat);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(72);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            imageFile.SaveFile(outputPath);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(78);
        }
        catch (COMException exception) when (options.PaperSize is "A3" or "Legal")
        {
            throw new InvalidOperationException(
                "A3 or Legal is not available on this Printer Model", exception);
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(FriendlyWiaMessage(exception), exception);
        }
        catch (Exception exception) when (
            options.PaperSize is "A3" or "Legal" &&
            exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "A3 or Legal is not available on this Printer Model", exception);
        }
        finally
        {
            ReleaseCom(imageFile);
            ReleaseCom(item);
            ReleaseCom(device);
            ReleaseCom(selectedInfo);
            ReleaseCom(manager);
        }
    });

    private static dynamic CreateDeviceManager()
    {
        var managerType = Type.GetTypeFromProgID("WIA.DeviceManager")
            ?? throw new InvalidOperationException(
                "Windows Image Acquisition (WIA) is unavailable. Install the Epson or Brother scanner driver and make sure the Windows Image Acquisition service is running.");
        return Activator.CreateInstance(managerType)
            ?? throw new InvalidOperationException("Windows could not start the scanner service.");
    }

    private static int IntentFor(ScanColorMode mode) => mode switch
    {
        ScanColorMode.Color => IntentColor,
        ScanColorMode.BlackAndWhite => IntentText,
        _ => IntentGrayscale
    };

    private static (double Width, double Height) PaperDimensions(string paperSize) => paperSize switch
    {
        // Some A3 WIA drivers, including the Brother DCP-6690CW, expose the
        // 300-DPI bed as 3507 pixels wide. These dimensions avoid asking the
        // driver for a single pixel beyond its advertised scan area.
        "A3" => (11.69, 16.53),
        "Letter" => (8.5, 11.0),
        "Legal" => (8.5, 14.0),
        _ => (8.2677, 11.6929)
    };

    private static bool CanUseScanArea(
        dynamic properties,
        string paperSize,
        int xResolution,
        int yResolution)
    {
        var (widthInches, heightInches) = PaperDimensions(paperSize);
        return TrySetExtent(properties, XExtent, (int)Math.Round(widthInches * xResolution)) &&
               TrySetExtent(properties, YExtent, (int)Math.Round(heightInches * yResolution));
    }

    private static int? ReadIntProperty(dynamic properties, int propertyId)
    {
        foreach (dynamic property in properties)
        {
            try
            {
                if ((int)property.PropertyID == propertyId)
                {
                    return Convert.ToInt32(property.Value);
                }
            }
            catch (Exception exception) when (
                exception is COMException or ArgumentException or InvalidCastException or FormatException)
            {
                return null;
            }
            finally
            {
                ReleaseCom(property);
            }
        }

        return null;
    }

    private static bool TrySetExtent(dynamic properties, int propertyId, int requestedValue)
    {
        // Some drivers advertise a bed that is one or two pixels shorter than the
        // mathematical paper size. Retrying within that tolerance avoids falling
        // back to the driver's smaller default rectangle.
        for (var adjustment = 0; adjustment <= 2; adjustment++)
        {
            if (TrySetProperty(properties, propertyId, Math.Max(1, requestedValue - adjustment)))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadProperty(dynamic properties, int propertyId, string propertyName)
    {
        foreach (dynamic property in properties)
        {
            try
            {
                if ((int)property.PropertyID == propertyId ||
                    string.Equals(Convert.ToString(property.Name), propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return Convert.ToString(property.Value);
                }
            }
            finally
            {
                ReleaseCom(property);
            }
        }

        return null;
    }

    private static bool TrySetProperty(
        dynamic properties,
        int propertyId,
        int value,
        bool requireExact = false)
    {
        foreach (dynamic property in properties)
        {
            try
            {
                if ((int)property.PropertyID != propertyId)
                {
                    continue;
                }

                try
                {
                    property.Value = value;
                    var appliedValue = Convert.ToInt32(property.Value);
                    return requireExact ? appliedValue == value : appliedValue >= value;
                }
                catch (Exception exception) when (
                    exception is COMException or ArgumentException or InvalidCastException)
                {
                    // Drivers often reject optional settings. Keeping the driver's current value
                    // allows scanning to continue while still applying supported settings.
                    return false;
                }
            }
            finally
            {
                ReleaseCom(property);
            }
        }

        return false;
    }

    private static string FriendlyWiaMessage(COMException exception) => exception.HResult switch
    {
        unchecked((int)0x80210003) => "The scanner is busy or not ready.",
        unchecked((int)0x80210005) => "The scanner is offline. Check its power and connection.",
        unchecked((int)0x80210006) => "The scanner cover is open.",
        unchecked((int)0x80210007) => "There is a paper jam in the scanner feeder.",
        unchecked((int)0x8021000A) => "The scanner feeder is empty.",
        unchecked((int)0x80210064) => "The scan was cancelled.",
        _ => $"The scanner could not complete the scan. {exception.Message}"
    };

    private static Task<T> RunSta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "WIA Scanner Thread"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static Task RunSta(Action action) => RunSta(() =>
    {
        action();
        return true;
    });

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch
            {
                // The COM runtime already released this wrapper.
            }
        }
    }
}
