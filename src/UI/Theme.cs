using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace AiUsageWidget.UI
{
    /// <summary>
    /// Shared light-dialog design tokens. The tray card is a dark floating
    /// surface; the dialogs are deliberately light, but they must share the same
    /// type, spacing rhythm, and control shapes so they read as one product.
    /// </summary>
    internal static class Theme
    {
        public static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily IconFont = new FontFamily("Segoe MDL2 Assets");

        public static readonly Brush Surface = Frozen(0xFF, 0xF7, 0xF8, 0xFA);
        public static readonly Brush CardBg = Frozen(0xFF, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Border = Frozen(0xFF, 0xE4, 0xE7, 0xEC);
        public static readonly Brush BorderStrong = Frozen(0xFF, 0xD0, 0xD5, 0xDD);
        public static readonly Brush Text = Frozen(0xFF, 0x10, 0x18, 0x28);
        public static readonly Brush Muted = Frozen(0xFF, 0x66, 0x70, 0x85);
        public static readonly Brush Faint = Frozen(0xFF, 0x98, 0xA2, 0xB3);
        public static readonly Brush Accent = Frozen(0xFF, 0x2F, 0x6F, 0xEB);
        public static readonly Brush AccentText = Frozen(0xFF, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Success = Frozen(0xFF, 0x0E, 0x7C, 0x5A);
        public static readonly Brush Danger = Frozen(0xFF, 0xB4, 0x23, 0x18);
        public static readonly Brush Track = Frozen(0xFF, 0xEE, 0xF0, 0xF4);

        private static ControlTemplate _buttonTemplate;
        private static ControlTemplate _transparentButtonTemplate;

        // ------------------------------------------------------------------ text

        public static TextBlock Section(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = UiFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Frozen(0xFF, 0x34, 0x40, 0x54),
                Margin = new Thickness(0, 0, 0, 6)
            };
        }

        public static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = UiFont,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = Text
            };
        }

        public static TextBlock Body(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = UiFont,
                FontSize = 12,
                Foreground = Text,
                TextWrapping = TextWrapping.Wrap
            };
        }

        public static TextBlock Caption(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = UiFont,
                FontSize = 10.5,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap
            };
        }

        public static TextBlock Footnote(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = UiFont,
                FontSize = 9.5,
                Foreground = Faint,
                TextWrapping = TextWrapping.Wrap
            };
        }

        // ---------------------------------------------------------------- layout

        public static Border Card(params UIElement[] children)
        {
            var stack = new StackPanel();
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i] != null) stack.Children.Add(children[i]);
            }

            return new Border
            {
                Background = CardBg,
                BorderBrush = Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 11, 14, 11),
                Child = stack
            };
        }

        public static Border Separator()
        {
            return new Border
            {
                Height = 1,
                Background = Border,
                Margin = new Thickness(0, 0, 0, 0)
            };
        }

        public static void Spacer(StackPanel stack, double height)
        {
            stack.Children.Add(new Border { Height = height });
        }

        // --------------------------------------------------------------- buttons

        public enum ButtonKind
        {
            Primary,
            Secondary,
            Ghost,
            Danger
        }

        public static Button Button(string text, ButtonKind kind, Action onClick)
        {
            var button = new Button
            {
                Content = text,
                FontFamily = UiFont,
                FontSize = 12,
                FontWeight = kind == ButtonKind.Primary ? FontWeights.SemiBold : FontWeights.Normal,
                Padding = new Thickness(14, 6, 14, 6),
                MinHeight = 30,
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = ButtonTemplate(),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            switch (kind)
            {
                case ButtonKind.Primary:
                    button.Background = Accent;
                    button.Foreground = AccentText;
                    button.BorderBrush = Accent;
                    break;
                case ButtonKind.Danger:
                    button.Background = Brushes.Transparent;
                    button.Foreground = Danger;
                    button.BorderBrush = Border;
                    break;
                case ButtonKind.Ghost:
                    button.Background = Brushes.Transparent;
                    button.Foreground = Muted;
                    button.BorderBrush = Brushes.Transparent;
                    break;
                default:
                    button.Background = CardBg;
                    button.Foreground = Text;
                    button.BorderBrush = BorderStrong;
                    break;
            }

            button.BorderThickness = kind == ButtonKind.Ghost ? new Thickness(0) : new Thickness(1);

            if (onClick != null)
            {
                button.Click += (s, e) => onClick();
            }

            AutomationProperties.SetName(button, text);
            return button;
        }

        /// <summary>
        /// Full-width row that behaves like a button: used by the statistics legend
        /// so each provider is clickable and reachable with Tab/Enter.
        /// </summary>
        public static Button RowButton(UIElement content, Action onClick, string automationName)
        {
            var button = new Button
            {
                Content = content,
                Template = TransparentTemplate(),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            if (onClick != null) button.Click += (s, e) => onClick();

            AutomationProperties.SetName(button, automationName);
            return button;
        }

        public static ControlTemplate TransparentTemplate()
        {
            if (_transparentButtonTemplate == null)
            {
                _transparentButtonTemplate = ParseTemplate(
                    "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                    "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                    "<Grid>" +
                    "<Border x:Name='Hover' Background='#00000000' CornerRadius='6'/>" +
                    "<ContentPresenter HorizontalAlignment='Stretch' VerticalAlignment='Center'/>" +
                    "</Grid>" +
                    "<ControlTemplate.Triggers>" +
                    "<Trigger Property='IsMouseOver' Value='True'>" +
                    "<Setter TargetName='Hover' Property='Background' Value='#0F101828'/>" +
                    "</Trigger>" +
                    "<Trigger Property='IsKeyboardFocused' Value='True'>" +
                    "<Setter TargetName='Hover' Property='Background' Value='#1A2F6FEB'/>" +
                    "</Trigger>" +
                    "<Trigger Property='IsEnabled' Value='False'>" +
                    "<Setter Property='Opacity' Value='0.45'/>" +
                    "</Trigger>" +
                    "</ControlTemplate.Triggers>" +
                    "</ControlTemplate>");
            }
            return _transparentButtonTemplate;
        }

        private static ControlTemplate ButtonTemplate()
        {
            if (_buttonTemplate == null)
            {
                _buttonTemplate = ParseTemplate(
                    "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                    "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                    "<Grid>" +
                    "<Border x:Name='Chrome' Background='{TemplateBinding Background}' " +
                    "BorderBrush='{TemplateBinding BorderBrush}' " +
                    "BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='6'/>" +
                    "<Border x:Name='Hover' Background='#00000000' CornerRadius='6'/>" +
                    "<Border x:Name='Ring' BorderBrush='#2F6FEB' BorderThickness='2' CornerRadius='6' Opacity='0'/>" +
                    "<ContentPresenter Margin='{TemplateBinding Padding}' " +
                    "HorizontalAlignment='Center' VerticalAlignment='Center' " +
                    "TextElement.Foreground='{TemplateBinding Foreground}'/>" +
                    "</Grid>" +
                    "<ControlTemplate.Triggers>" +
                    "<Trigger Property='IsMouseOver' Value='True'>" +
                    "<Setter TargetName='Hover' Property='Background' Value='#14000000'/>" +
                    "</Trigger>" +
                    "<Trigger Property='IsPressed' Value='True'>" +
                    "<Setter TargetName='Hover' Property='Background' Value='#24000000'/>" +
                    "</Trigger>" +
                    "<Trigger Property='IsKeyboardFocused' Value='True'>" +
                    "<Setter TargetName='Ring' Property='Opacity' Value='1'/>" +
                    "</Trigger>" +
                    "<Trigger Property='IsEnabled' Value='False'>" +
                    "<Setter Property='Opacity' Value='0.45'/>" +
                    "</Trigger>" +
                    "</ControlTemplate.Triggers>" +
                    "</ControlTemplate>");
            }
            return _buttonTemplate;
        }

        private static ControlTemplate ParseTemplate(string xaml)
        {
            return (ControlTemplate)XamlReader.Parse(xaml);
        }

        public static Brush Frozen(byte alpha, byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
            brush.Freeze();
            return brush;
        }
    }
}



