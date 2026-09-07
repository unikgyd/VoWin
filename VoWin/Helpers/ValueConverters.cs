using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.VoWifi;
using VoWin.Models;
using Wpf.Ui.Controls;

namespace VoWin.Helpers
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool val = value is bool b && b;
            if (Invert) val = !val;
            return val ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isVis = value is Visibility v && v == Visibility.Visible;
            return Invert ? !isVis : isVis;
        }
    }

    public class NullToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isNull = value == null || (value is string s && string.IsNullOrEmpty(s));
            if (Invert) isNull = !isNull;
            return isNull ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class CountToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            int count = 0;
            if (value is int i) count = i;
            else if (value is System.Collections.ICollection c) count = c.Count;

            bool isEmpty = count == 0;
            if (Invert) isEmpty = !isEmpty;
            return isEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class SmsDirectionToAlignmentConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isOutgoing && isOutgoing)
                return HorizontalAlignment.Right;
            return HorizontalAlignment.Left;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class SmsDirectionToMarginConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isOutgoing && isOutgoing)
                return new Thickness(80, 4, 12, 4);
            return new Thickness(12, 4, 80, 4);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class SmsDirectionToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isOutgoing = false;
            if (value is bool b) isOutgoing = b;
            else if (value != null && value.ToString() == "Outgoing") isOutgoing = true;

            if (isOutgoing)
            {
                return new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)); // Modern Blue 600
            }

            // In Dark Mode, resolve to dark card background; in Light Mode, resolve to light card background
            return Application.Current?.TryFindResource("CardBackgroundFillColorDefaultBrush") as Brush
                ?? Application.Current?.TryFindResource("ControlFillColorDefaultBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class SmsDirectionToForegroundConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isOutgoing = false;
            if (value is bool b) isOutgoing = b;
            else if (value != null && value.ToString() == "Outgoing") isOutgoing = true;

            if (isOutgoing)
            {
                return Brushes.White;
            }

            // In Dark Mode, resolve to light/white text; in Light Mode, resolve to dark text
            return Application.Current?.TryFindResource("TextFillColorPrimaryBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class CallDirectionToSymbolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is CallDirection dir)
            {
                return dir == CallDirection.Incoming ? SymbolRegular.CallInbound24 : SymbolRegular.CallOutbound24;
            }
            return SymbolRegular.Call24;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class CallDirectionToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is CallDirection dir)
            {
                return dir == CallDirection.Incoming
                    ? new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)) // Blue
                    : new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69)); // Emerald
            }
            return new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class CallDirectionToStringConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is CallDirection dir)
            {
                return dir == CallDirection.Incoming ? "呼入" : "呼出";
            }
            return "未知";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class CallStateToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is CallState state)
            {
                return state switch
                {
                    CallState.Active => new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69)), // Emerald
                    CallState.Dialing or CallState.Ringing or CallState.Incoming => new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), // Blue
                    CallState.Held => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)), // Amber
                    CallState.Ended => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)), // Red
                    _ => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
                };
            }
            return new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class VoWifiStateToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is VoWifiState state)
            {
                return state switch
                {
                    VoWifiState.ImsRegistered => new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69)), // Emerald
                    VoWifiState.IpsecTunnelEstablished or VoWifiState.ImsRegistering => new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), // Blue
                    VoWifiState.ResolvingEpdg or VoWifiState.ConnectingIkev2 or VoWifiState.AuthenticatingEapAka => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)), // Amber
                    VoWifiState.Failed => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)), // Red
                    _ => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
                };
            }
            return new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class SlotStateToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is SlotState state)
            {
                return state switch
                {
                    SlotState.Online => new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69)), // Emerald
                    SlotState.Busy => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),   // Amber
                    SlotState.Error => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),  // Red
                    _ => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
                };
            }
            return new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class SignalBarsToSymbolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            int bars = value is int b ? b : 0;
            return bars switch
            {
                >= 4 => SymbolRegular.CellularData524,
                3 => SymbolRegular.CellularData424,
                2 => SymbolRegular.CellularData324,
                1 => SymbolRegular.CellularData224,
                _ => SymbolRegular.CellularOff24
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToSuccessDangerBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool ok = value is bool b && b;
            return ok
                ? new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69)) // Emerald 600
                : new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)); // Red 600
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToSuccessDangerBackgroundConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool ok = value is bool b && b;
            return ok
                ? new SolidColorBrush(Color.FromRgb(0xEC, 0xFD, 0xF5)) // Soft Emerald 50
                : new SolidColorBrush(Color.FromRgb(0xFE, 0xF2, 0xF2)); // Soft Red 50
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToSuccessDangerSymbolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool ok = value is bool b && b;
            return ok ? SymbolRegular.Checkmark24 : SymbolRegular.Dismiss24;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class CallRecordToSymbolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is CallRecordModel record)
            {
                if (record.Direction == CallDirection.Missed)
                    return SymbolRegular.CallDismiss24;
                if (record.Duration == TimeSpan.Zero && record.Direction == CallDirection.Outgoing)
                    return SymbolRegular.Warning24;
                return record.Direction == CallDirection.Incoming
                    ? SymbolRegular.CallInbound24
                    : SymbolRegular.CallOutbound24;
            }
            return SymbolRegular.Call24;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class CallRecordToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is CallRecordModel record)
            {
                if (record.Direction == CallDirection.Missed)
                    return new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)); // Red 600
                if (record.Duration == TimeSpan.Zero && record.Direction == CallDirection.Outgoing)
                    return new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // Amber/Orange 500
                return record.Direction == CallDirection.Incoming
                    ? new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)) // Blue 600
                    : new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)); // Green 600
            }
            return new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
