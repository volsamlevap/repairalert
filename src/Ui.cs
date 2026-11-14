using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RepairAlert;

internal static class Ui
{
    // Светлая тема — по дизайн-спецификации (§2.1)
    public static Brush Background = FrozenBrush("#F5F5F7");
    public static Brush Surface = FrozenBrush("#FFFFFF");
    public static Brush Ink = FrozenBrush("#1D1D1F");
    public static Brush Muted = FrozenBrush("#6E6E73");
    public static Brush Tertiary = FrozenBrush("#A1A1A6");
    public static Brush Accent = FrozenBrush("#0071E3");
    public static Brush AccentHover = FrozenBrush("#0077ED");
    public static Brush AccentSoft = FrozenBrush("#E8F1FC");
    public static Brush Dark = FrozenBrush("#1D1D1F");
    public static Brush Green = FrozenBrush("#34C759");
    public static Brush Warning = FrozenBrush("#FF9F0A");
    public static Brush Danger = FrozenBrush("#FF3B30");
    public static Brush Border = FrozenBrush("#D2D2D7");
    public static Brush Separator = FrozenBrush("#E5E5EA");
    public static Brush FillSubtle = FrozenBrush("#EFEFF2");
    public static Brush Secondary = FrozenBrush("#EFEFF2");
    // Поле ввода чата: на пару тонов темнее светлого фона, чтобы читалось как плашка
    public static Brush ComposerFill = FrozenBrush("#E8E8ED");
    public static Brush ScrollThumb = FrozenBrush("#C7C7CC");

    public static bool IsDarkTheme { get; private set; }

    /// <summary>Применяет тему до создания окон: "dark", "light" или "system" (как в Windows).</summary>
    public static void ApplyTheme(string setting)
    {
        var dark = setting == "dark" || (setting != "light" && SystemThemeIsDark());
        IsDarkTheme = dark;
        if (!dark) return;
        // Тёмная тема — по дизайн-спецификации (§2.2)
        Background = FrozenBrush("#1C1C1E");
        Surface = FrozenBrush("#2C2C2E");
        Ink = FrozenBrush("#F5F5F7");
        Muted = FrozenBrush("#A1A1A6");
        Tertiary = FrozenBrush("#6E6E73");
        Accent = FrozenBrush("#0A84FF");
        AccentHover = FrozenBrush("#2E93FF");
        AccentSoft = FrozenBrush("#253E58");
        Dark = FrozenBrush("#111113");
        Green = FrozenBrush("#30D158");
        Warning = FrozenBrush("#FF9F0A");
        Danger = FrozenBrush("#FF453A");
        Border = FrozenBrush("#48484A");
        Separator = FrozenBrush("#38383A");
        FillSubtle = FrozenBrush("#3A3A3C");
        Secondary = FrozenBrush("#3A3A3C");
        // Поле ввода чата: на пару тонов светлее тёмного фона, как в Telegram/Apple
        ComposerFill = FrozenBrush("#4A4A4F");
        ScrollThumb = FrozenBrush("#5A5A5E");
    }

    private static bool SystemThemeIsDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }

    private static SolidColorBrush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    public static void ApplyResources(System.Windows.Application app)
    {
        app.Resources["WindowBackground"] = Background;
        app.Resources["SurfaceBrush"] = Surface;
        app.Resources["InkBrush"] = Ink;
        app.Resources["MutedBrush"] = Muted;
        app.Resources["TertiaryBrush"] = Tertiary;
        app.Resources["AccentBrush"] = Accent;
        app.Resources["AccentSoftBrush"] = AccentSoft;
        app.Resources["DarkBrush"] = Dark;
        app.Resources["SuccessBrush"] = Green;
        app.Resources["WarningBrush"] = Warning;
        app.Resources["DangerBrush"] = Danger;
        app.Resources["BorderBrush"] = Border;
        app.Resources["SeparatorBrush"] = Separator;
        app.Resources["FillSubtleBrush"] = FillSubtle;
        app.Resources["SecondaryBrush"] = Secondary;
        app.Resources["ScrollThumbBrush"] = ScrollThumb;

        var baseButton = new Style(typeof(Button));
        baseButton.Setters.Add(new Setter(Control.MinHeightProperty, 36.0));
        baseButton.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 6, 16, 6)));
        baseButton.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        baseButton.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        baseButton.Setters.Add(new Setter(Control.BackgroundProperty, Secondary));
        baseButton.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        baseButton.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        baseButton.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        baseButton.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        baseButton.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        baseButton.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 2, 0, 2)));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetValue(System.Windows.Controls.Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(System.Windows.Controls.Border.BorderBrushProperty,
            new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty,
            new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(System.Windows.Controls.Border.PaddingProperty,
            new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(System.Windows.Controls.Border.MinHeightProperty,
            new TemplateBindingExtension(Control.MinHeightProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.82));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.38));
        template.Triggers.Add(disabled);
        baseButton.Setters.Add(new Setter(Control.TemplateProperty, template));
        app.Resources[typeof(Button)] = baseButton;

        var primary = new Style(typeof(Button), baseButton);
        primary.Setters.Add(new Setter(Control.BackgroundProperty, Accent));
        primary.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        app.Resources["PrimaryButton"] = primary;
        var dark = new Style(typeof(Button), baseButton);
        dark.Setters.Add(new Setter(Control.BackgroundProperty, Dark));
        dark.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        app.Resources["DarkButton"] = dark;
        var success = new Style(typeof(Button), baseButton);
        success.Setters.Add(new Setter(Control.BackgroundProperty, Green));
        success.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        app.Resources["SuccessButton"] = success;
        var danger = new Style(typeof(Button), baseButton);
        danger.Setters.Add(new Setter(Control.BackgroundProperty, FillSubtle));
        danger.Setters.Add(new Setter(Control.ForegroundProperty, Danger));
        app.Resources["DangerButton"] = danger;
        var white = new Style(typeof(Button), baseButton);
        white.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        white.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        white.Setters.Add(new Setter(Control.BorderBrushProperty, Border));
        white.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        app.Resources["WhiteButton"] = white;
        var quiet = new Style(typeof(Button), baseButton);
        quiet.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        quiet.Setters.Add(new Setter(Control.ForegroundProperty, Muted));
        quiet.Setters.Add(new Setter(Control.MinHeightProperty, 32.0));
        quiet.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
        quiet.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        quiet.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Normal));
        app.Resources["QuietButton"] = quiet;
        var chip = new Style(typeof(Button), baseButton);
        chip.Setters.Add(new Setter(Control.BackgroundProperty, FillSubtle));
        chip.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        chip.Setters.Add(new Setter(Control.MinHeightProperty, 28.0));
        chip.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 0, 12, 0)));
        chip.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        chip.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Normal));
        app.Resources["ChipButton"] = chip;

        app.Resources[typeof(TextBox)] = Parse<Style>(TextBoxStyleXaml);
        app.Resources[typeof(ComboBox)] = Parse<Style>(ComboBoxStyleXaml);
        app.Resources[typeof(ComboBoxItem)] = Parse<Style>(ComboBoxItemStyleXaml);
        app.Resources[typeof(CheckBox)] = Parse<Style>(CheckBoxStyleXaml);
        app.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = Parse<Style>(ScrollBarStyleXaml);
        app.Resources[typeof(ProgressBar)] = Parse<Style>(ProgressBarStyleXaml);
        // Контекстные меню (правый клик по сообщению, «Отложить ▾») — в цветах темы оформления
        app.Resources[typeof(ContextMenu)] = Parse<Style>(ContextMenuStyleXaml);
        app.Resources[typeof(MenuItem)] = Parse<Style>(MenuItemStyleXaml);

        var text = new Style(typeof(TextBlock));
        text.Setters.Add(new Setter(TextBlock.ForegroundProperty, Ink));
        text.Setters.Add(new Setter(TextBlock.FontSizeProperty, 14.0));
        app.Resources[typeof(TextBlock)] = text;
        var muted = new Style(typeof(TextBlock), text);
        muted.Setters.Add(new Setter(TextBlock.ForegroundProperty, Muted));
        muted.Setters.Add(new Setter(TextBlock.FontSizeProperty, 13.0));
        muted.Setters.Add(new Setter(TextBlock.LineHeightProperty, 20.0));
        muted.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        app.Resources["MutedText"] = muted;
        var section = new Style(typeof(TextBlock), text);
        section.Setters.Add(new Setter(TextBlock.FontSizeProperty, 16.0));
        section.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        section.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(0, 12, 0, 6)));
        app.Resources["SectionTitle"] = section;
    }

    private static T Parse<T>(string xaml) =>
        (T)System.Windows.Markup.XamlReader.Parse(xaml);

    private const string X = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private static readonly string TextBoxStyleXaml = """
        <Style %X% TargetType="TextBox">
            <Setter Property="MinHeight" Value="38"/>
            <Setter Property="Padding" Value="12,0,12,0"/>
            <Setter Property="FontSize" Value="14"/>
            <Setter Property="Background" Value="{DynamicResource FillSubtleBrush}"/>
            <Setter Property="Foreground" Value="{DynamicResource InkBrush}"/>
            <Setter Property="CaretBrush" Value="{DynamicResource InkBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource SeparatorBrush}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Margin" Value="0,3,0,3"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="TextBox">
                        <Border x:Name="Bd" Background="{TemplateBinding Background}"
                                BorderBrush="{TemplateBinding BorderBrush}"
                                BorderThickness="{TemplateBinding BorderThickness}"
                                CornerRadius="10" Padding="{TemplateBinding Padding}"
                                MinHeight="{TemplateBinding MinHeight}">
                            <!-- Вертикальное выравнивание содержимого обязано пробрасываться
                                 в ScrollViewer: иначе VerticalContentAlignment у полей игнорируется,
                                 и курсор с текстом прилипают к верхнему краю (2.39) -->
                            <ScrollViewer x:Name="PART_ContentHost" Focusable="False"
                                VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsKeyboardFocused" Value="True">
                                <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/>
                            </Trigger>
                            <Trigger Property="IsReadOnly" Value="True">
                                <Setter TargetName="Bd" Property="Opacity" Value="0.7"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
            <Style.Triggers>
                <!-- Многострочные поля (текст прижат к верху): отступ сверху и снизу,
                     чтобы курсор не упирался в рамку, — визуально как отступ слева.
                     Поля со своим Padding (ввод сообщения в чате) это не затрагивает -->
                <Trigger Property="AcceptsReturn" Value="True">
                    <Setter Property="Padding" Value="12,10,12,10"/>
                </Trigger>
            </Style.Triggers>
        </Style>
        """.Replace("%X%", X);

    // Всплывающее контекстное меню: скруглённая карточка в цветах темы, как выпадающий список
    private static readonly string ContextMenuStyleXaml = """
        <Style %X% TargetType="ContextMenu">
            <Setter Property="Background" Value="{DynamicResource SurfaceBrush}"/>
            <Setter Property="Foreground" Value="{DynamicResource InkBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource SeparatorBrush}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="HasDropShadow" Value="False"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ContextMenu">
                        <Border Background="{TemplateBinding Background}"
                                BorderBrush="{TemplateBinding BorderBrush}"
                                BorderThickness="{TemplateBinding BorderThickness}"
                                CornerRadius="12" Padding="5" MinWidth="190">
                            <Border.Effect>
                                <DropShadowEffect BlurRadius="22" ShadowDepth="3" Opacity="0.25" Color="Black"/>
                            </Border.Effect>
                            <ScrollViewer CanContentScroll="True">
                                <ItemsPresenter/>
                            </ScrollViewer>
                        </Border>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """.Replace("%X%", X);

    // Пункт меню: иконка слева, текст в цвете темы, подсветка при наведении мягким акцентом
    private static readonly string MenuItemStyleXaml = """
        <Style %X% TargetType="MenuItem">
            <Setter Property="Foreground" Value="{DynamicResource InkBrush}"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="MinHeight" Value="32"/>
            <Setter Property="Padding" Value="9,3,14,3"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="MenuItem">
                        <Border x:Name="Bd" Background="Transparent" CornerRadius="8"
                                Padding="{TemplateBinding Padding}">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="27"/>
                                    <ColumnDefinition Width="*"/>
                                </Grid.ColumnDefinitions>
                                <ContentPresenter Grid.Column="0" VerticalAlignment="Center"
                                    Content="{TemplateBinding Icon}"/>
                                <ContentPresenter Grid.Column="1" VerticalAlignment="Center"
                                    Content="{TemplateBinding Header}"
                                    ContentTemplate="{TemplateBinding HeaderTemplate}"
                                    ContentStringFormat="{TemplateBinding HeaderStringFormat}"
                                    TextBlock.Foreground="{TemplateBinding Foreground}"/>
                            </Grid>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsHighlighted" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource AccentSoftBrush}"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Opacity" Value="0.45"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """.Replace("%X%", X);

    private static readonly string ComboBoxStyleXaml = """
        <Style %X% TargetType="ComboBox">
            <Setter Property="MinHeight" Value="38"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="Background" Value="{DynamicResource FillSubtleBrush}"/>
            <Setter Property="Foreground" Value="{DynamicResource InkBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource SeparatorBrush}"/>
            <Setter Property="Margin" Value="0,3,0,3"/>
            <Setter Property="HorizontalContentAlignment" Value="Left"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ComboBox">
                        <Grid>
                            <ToggleButton x:Name="PART_ToggleButton" Focusable="False" ClickMode="Press"
                                IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}"
                                Background="{TemplateBinding Background}"
                                BorderBrush="{TemplateBinding BorderBrush}"
                                MinHeight="{TemplateBinding MinHeight}">
                                <ToggleButton.Template>
                                    <ControlTemplate TargetType="ToggleButton">
                                        <Border Background="{TemplateBinding Background}"
                                                BorderBrush="{TemplateBinding BorderBrush}"
                                                BorderThickness="1" CornerRadius="10"
                                                MinHeight="{TemplateBinding MinHeight}">
                                            <TextBlock Text="▾" Foreground="{DynamicResource MutedBrush}"
                                                       FontSize="12" HorizontalAlignment="Right"
                                                       VerticalAlignment="Center" Margin="0,0,12,0"/>
                                        </Border>
                                    </ControlTemplate>
                                </ToggleButton.Template>
                            </ToggleButton>
                            <ContentPresenter IsHitTestVisible="False" VerticalAlignment="Center"
                                HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                Margin="12,0,30,0"
                                Content="{Binding SelectionBoxItem, RelativeSource={RelativeSource TemplatedParent}}"
                                ContentTemplate="{Binding SelectionBoxItemTemplate, RelativeSource={RelativeSource TemplatedParent}}"/>
                            <Popup x:Name="PART_Popup" AllowsTransparency="True" Placement="Bottom"
                                IsOpen="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}"
                                PlacementTarget="{Binding RelativeSource={RelativeSource TemplatedParent}}"
                                PopupAnimation="Slide">
                                <Grid MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                                      MaxHeight="320" Margin="0,4,0,0">
                                    <Border Background="{DynamicResource SurfaceBrush}"
                                            BorderBrush="{DynamicResource SeparatorBrush}"
                                            BorderThickness="1" CornerRadius="12" Padding="4">
                                        <Border.Effect>
                                            <DropShadowEffect BlurRadius="16" ShadowDepth="2" Opacity="0.18" Color="Black"/>
                                        </Border.Effect>
                                        <ScrollViewer VerticalScrollBarVisibility="Auto">
                                            <ItemsPresenter/>
                                        </ScrollViewer>
                                    </Border>
                                </Grid>
                            </Popup>
                        </Grid>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """.Replace("%X%", X);

    private static readonly string ComboBoxItemStyleXaml = """
        <Style %X% TargetType="ComboBoxItem">
            <Setter Property="Padding" Value="10,7,10,7"/>
            <Setter Property="Foreground" Value="{DynamicResource InkBrush}"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ComboBoxItem">
                        <Border x:Name="Bd" Background="Transparent" CornerRadius="8"
                                Padding="{TemplateBinding Padding}">
                            <ContentPresenter VerticalAlignment="Center"
                                HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource FillSubtleBrush}"/>
                            </Trigger>
                            <Trigger Property="IsSelected" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource AccentSoftBrush}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """.Replace("%X%", X);

    private static readonly string CheckBoxStyleXaml = """
        <Style %X% TargetType="CheckBox">
            <Setter Property="Foreground" Value="{DynamicResource InkBrush}"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="CheckBox">
                        <StackPanel Orientation="Horizontal">
                            <Border x:Name="Box" Width="18" Height="18" CornerRadius="5"
                                    BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1.5"
                                    Background="Transparent" VerticalAlignment="Center">
                                <TextBlock x:Name="Mark" Text="✓" Foreground="White" FontSize="12"
                                           FontWeight="Bold" HorizontalAlignment="Center"
                                           VerticalAlignment="Center" Visibility="Collapsed"/>
                            </Border>
                            <ContentPresenter Margin="8,0,0,0" VerticalAlignment="Center"/>
                        </StackPanel>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsChecked" Value="True">
                                <Setter TargetName="Box" Property="Background" Value="{DynamicResource AccentBrush}"/>
                                <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/>
                                <Setter TargetName="Mark" Property="Visibility" Value="Visible"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Opacity" Value="0.45"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """.Replace("%X%", X);

    private static readonly string ScrollBarStyleXaml = """
        <Style %X% TargetType="ScrollBar">
            <Setter Property="Width" Value="10"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ScrollBar">
                        <Grid>
                            <Track x:Name="PART_Track" IsDirectionReversed="True">
                                <Track.Thumb>
                                    <Thumb>
                                        <Thumb.Template>
                                            <ControlTemplate TargetType="Thumb">
                                                <Border CornerRadius="5" Margin="2,1,2,1"
                                                        Background="{DynamicResource ScrollThumbBrush}"/>
                                            </ControlTemplate>
                                        </Thumb.Template>
                                    </Thumb>
                                </Track.Thumb>
                            </Track>
                        </Grid>
                        <ControlTemplate.Triggers>
                            <Trigger Property="Orientation" Value="Horizontal">
                                <Setter Property="Height" Value="10"/>
                                <Setter Property="Width" Value="Auto"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """.Replace("%X%", X);

    private static readonly string ProgressBarStyleXaml = """
        <Style %X% TargetType="ProgressBar">
            <Setter Property="Height" Value="5"/>
            <Setter Property="Foreground" Value="{DynamicResource AccentBrush}"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ProgressBar">
                        <Grid>
                            <Border x:Name="PART_Track" Background="{DynamicResource FillSubtleBrush}" CornerRadius="3"/>
                            <Border x:Name="PART_Indicator" Background="{TemplateBinding Foreground}"
                                    CornerRadius="3" HorizontalAlignment="Left"/>
                        </Grid>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """.Replace("%X%", X);

    public static void Prepare(Window window, string title, double width, double height,
        double minWidth = 420, double minHeight = 480)
    {
        window.Title = title;
        window.Icon = new BitmapImage(new Uri("pack://application:,,,/assets/app-icon.png", UriKind.Absolute));
        window.Width = width;
        window.Height = height;
        window.MinWidth = minWidth;
        window.MinHeight = minHeight;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Background = Background;
        window.Foreground = Ink;
        window.FontFamily = new FontFamily("Segoe UI");
        window.FontSize = 14;
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.ClearType);
        RenderOptions.SetBitmapScalingMode(window, BitmapScalingMode.HighQuality);
        window.SourceInitialized += (_, _) => ApplyWindowChrome(window);
        window.Loaded += (_, _) => FitToWorkArea(window);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(System.IntPtr hwnd, int attribute,
        ref int value, int size);

    private static void ApplyWindowChrome(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == System.IntPtr.Zero) return;
            if (IsDarkTheme)
            {
                var dark = 1;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            var corner = 2;
            DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE = round
        }
        catch { }
    }

    public static Button ActionButton(string text, string style = "", string iconGeometry = "")
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Height = style == "QuietButton" ? 36 : 44
        };
        var content = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.SetBinding(TextBlock.ForegroundProperty,
            new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
        if (iconGeometry.Length > 0)
        {
            // Векторная иконка слева от текста; цвет привязан к цвету текста кнопки
            var icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(iconGeometry),
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.SetBinding(System.Windows.Shapes.Path.FillProperty,
                new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
            content.Margin = new Thickness(7, 0, 0, 0);
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            row.Children.Add(icon);
            row.Children.Add(content);
            button.Content = row;
        }
        else
        {
            button.Content = content;
        }
        if (style.Length > 0) button.SetResourceReference(Control.StyleProperty, style);
        return button;
    }

    public static Grid ActionGrid(params Button[] buttons)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = (buttons.Length + 1) / 2;
        for (var i = 0; i < rows; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            button.Margin = new Thickness(i % 2 == 0 ? 0 : 4, 3, i % 2 == 0 ? 4 : 0, 3);
            Grid.SetRow(button, i / 2);
            Grid.SetColumn(button, i % 2);
            grid.Children.Add(button);
        }
        return grid;
    }

    public static Border Header(string role, string subtitle, Action switchRole)
    {
        var header = new Border { Background = Dark, Padding = new Thickness(18, 14, 18, 14) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        texts.Children.Add(new TextBlock
        {
            Text = "СЕРВИС ЛЕГКО",
            Foreground = Brushes.White,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap
        });
        texts.Children.Add(new TextBlock
        {
            Text = role,
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.NoWrap
        });
        if (!string.IsNullOrWhiteSpace(subtitle)) texts.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = FrozenBrush("#D2D2D7"),
            FontSize = 11,
            LineHeight = 16,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        grid.Children.Add(texts);
        var button = ActionButton("Сменить роль");
        button.Width = 128;
        button.Height = 36;
        button.FontSize = 11;
        button.Padding = new Thickness(9, 5, 9, 5);
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Margin = new Thickness(0);
        button.Click += (_, _) => switchRole();
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        header.Child = grid;
        return header;
    }

    public static Border Card(StackPanel content)
    {
        content.Margin = new Thickness(16);
        return new Border
        {
            Child = content,
            Background = Surface,
            BorderBrush = Separator,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(0, 0, 0, 10),
            SnapsToDevicePixels = true
        };
    }

    public static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.NoWrap,
        Margin = new Thickness(0, 12, 0, 6)
    };

    public static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = Muted,
        FontSize = 12,
        LineHeight = 18,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 7)
    };

    // Векторная шестерёнка (24×24) для кнопки «Настройки» — в едином стиле иконок программы
    public const string IconSettings = "M19.14,12.94c0.04-0.3,0.06-0.61,0.06-0.94c0-0.32-0.02-0.64-0.07-0.94l2.03-1.58c0.18-0.14,0.23-0.41,0.12-0.61l-1.92-3.32c-0.12-0.22-0.37-0.29-0.59-0.22l-2.39,0.96c-0.5-0.38-1.03-0.7-1.62-0.94L14.4,2.81c-0.04-0.24-0.24-0.41-0.48-0.41h-3.84c-0.24,0-0.43,0.17-0.47,0.41L9.25,5.35C8.66,5.59,8.12,5.92,7.63,6.29L5.24,5.33c-0.22-0.08-0.47,0-0.59,0.22L2.74,8.87C2.62,9.08,2.66,9.34,2.86,9.48l2.03,1.58C4.84,11.36,4.8,11.69,4.8,12s0.02,0.64,0.07,0.94l-2.03,1.58c-0.18,0.14-0.23,0.41-0.12,0.61l1.92,3.32c0.12,0.22,0.37,0.29,0.59,0.22l2.39-0.96c0.5,0.38,1.03,0.7,1.62,0.94l0.36,2.54c0.05,0.24,0.24,0.41,0.48,0.41h3.84c0.24,0,0.44-0.17,0.47-0.41l0.36-2.54c0.59-0.24,1.13-0.56,1.62-0.94l2.39,0.96c0.22,0.08,0.47,0,0.59-0.22l1.92-3.32c0.12-0.22,0.07-0.47-0.12-0.61L19.14,12.94zM12,15.6c-1.98,0-3.6-1.62-3.6-3.6s1.62-3.6,3.6-3.6s3.6,1.62,3.6,3.6S13.98,15.6,12,15.6z";

    private static BitmapImage? _logoImage;

    /// <summary>Фирменный значок программы — тот же файл, что у ярлыка запуска и в трее.</summary>
    public static BitmapImage LogoImage
    {
        get
        {
            if (_logoImage is null)
            {
                _logoImage = new BitmapImage(new Uri("pack://application:,,,/assets/app-icon.png", UriKind.Absolute));
                _logoImage.Freeze();
            }
            return _logoImage;
        }
    }

    public static Border LogoMark(double size = 30)
    {
        // Внутри программы показываем тот же логотип, что и у ярлыка запуска,
        // а не текстовую букву — значок везде один и тот же
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.28),
            Background = Dark
        };
        border.Child = new Image
        {
            Source = LogoImage,
            Stretch = Stretch.UniformToFill,
            Clip = new RectangleGeometry(new Rect(0, 0, size, size), size * 0.28, size * 0.28)
        };
        return border;
    }

    public static Border StatusDot(Brush brush, double size = 9) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = brush,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>Мягкий статусный бейдж по дизайну §5.4: пилюля 22px, точка + подпись,
    /// фон — цвет статуса при ~12% прозрачности, текст — цвет статуса.</summary>
    public static Border Badge(string text, Brush color)
    {
        var c = color is SolidColorBrush solid ? solid.Color : Colors.Gray;
        var soft = new SolidColorBrush(Color.FromArgb(31, c.R, c.G, c.B));
        soft.Freeze();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = color,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = color,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        return new Border
        {
            Background = soft,
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(8, 0, 10, 0),
            MinHeight = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Child = row
        };
    }

    public sealed class SegmentedControl : Grid
    {
        private readonly Button[] _buttons;
        private int _selected;
        public event Action<int>? SelectionChanged;

        public SegmentedControl(string[] items, int selected = 0)
        {
            _buttons = new Button[items.Length];
            var wrap = new Border
            {
                Background = FillSubtle,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(2)
            };
            var inner = new Grid();
            for (var i = 0; i < items.Length; i++)
                inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < items.Length; i++)
            {
                var index = i;
                var label = new TextBlock
                {
                    Text = items[i],
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var button = new Button
                {
                    MinHeight = 30,
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0),
                    Cursor = Cursors.Hand,
                    BorderThickness = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                label.SetBinding(TextBlock.ForegroundProperty,
                    new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
                button.Content = label;
                var template = new ControlTemplate(typeof(Button));
                var border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
                border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
                border.SetValue(System.Windows.Controls.Border.BackgroundProperty,
                    new TemplateBindingExtension(Control.BackgroundProperty));
                var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
                presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                border.AppendChild(presenter);
                template.VisualTree = border;
                button.Template = template;
                button.Click += (_, _) => Select(index, true);
                Grid.SetColumn(button, i);
                inner.Children.Add(button);
                _buttons[i] = button;
            }
            wrap.Child = inner;
            Children.Add(wrap);
            Select(selected, false);
        }

        public void Select(int index, bool notify)
        {
            _selected = index;
            for (var i = 0; i < _buttons.Length; i++)
            {
                var active = i == index;
                _buttons[i].Background = active ? Surface : Brushes.Transparent;
                _buttons[i].Foreground = active ? Ink : Muted;
            }
            if (notify) SelectionChanged?.Invoke(index);
        }

        public int SelectedIndex => _selected;
    }

    public static FrameworkElement EmptyState(string title, string subtitle, string actionText = "", Action? action = null)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 380
        };
        panel.Children.Add(new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(18),
            Background = FillSubtle,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = LogoMark(44)
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 6)
        });
        panel.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = Muted,
            FontSize = 13,
            LineHeight = 19,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        if (actionText.Length > 0 && action is not null)
        {
            var button = ActionButton(actionText, "PrimaryButton");
            button.Width = 220;
            button.Margin = new Thickness(0, 18, 0, 0);
            button.Click += (_, _) => action();
            panel.Children.Add(button);
        }
        return panel;
    }

    private static void FitToWorkArea(Window window)
    {
        var area = SystemParameters.WorkArea;
        var maxWidth = Math.Max(280, area.Width - 16);
        var maxHeight = Math.Max(280, area.Height - 16);
        window.MinWidth = Math.Min(window.MinWidth, maxWidth);
        window.MinHeight = Math.Min(window.MinHeight, maxHeight);
        window.MaxWidth = maxWidth;
        window.MaxHeight = maxHeight;
        if (window.Width > maxWidth) window.Width = maxWidth;
        if (window.Height > maxHeight) window.Height = maxHeight;
        window.Left = Math.Max(area.Left, area.Left + (area.Width - window.Width) / 2);
        window.Top = Math.Max(area.Top, area.Top + (area.Height - window.Height) / 2);
    }
}
