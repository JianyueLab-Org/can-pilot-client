using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CanClient.Core;

namespace CanClient.Converters;

/// <summary>按消息类别着色。颜色定义在 Tokens.axaml 中，键名为 Brush.Message.{Kind}。</summary>
public sealed class MessageKindToBrushConverter : IValueConverter
{
    public static MessageKindToBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value as MessageKind? ?? MessageKind.System;
        var app = Application.Current;
        return app?.TryGetResource($"Brush.Message.{kind}", app.ActualThemeVariant, out var brush) == true
            ? brush as IBrush
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
