using System.Globalization;
using Avalonia.Data.Converters;

namespace DustoreLauncherV.Mac.Controls;

public static class Converters
{
    /// <summary>Display headings are set in capitals, like the Windows launcher.</summary>
    public static readonly IValueConverter Upper = new FuncValueConverter<string?, string>(text => (text ?? "").ToUpper(CultureInfo.CurrentCulture));
}
