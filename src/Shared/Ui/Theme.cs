using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace InnoPro.Ui;

/// <summary>
/// Giao diện tự vẽ dùng chung cho mọi cửa sổ của vn-revit-mcp (add-in và module chat): không dùng template mặc định
/// của Windows để hiển thị nhất quán và tránh lỗi theme. File được biên dịch kèm vào từng assembly (linked source).
/// </summary>
internal static class Theme
{
    public static readonly Color BrandColor = Color.FromRgb(0x0B, 0x5C, 0xAD);
    public static readonly Brush Brand = Freeze(new SolidColorBrush(BrandColor));
    public static readonly Brush Text = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28)));
    public static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x65, 0x6D, 0x76)));
    public static readonly Brush Line = Freeze(new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xDE)));
    public static readonly Brush Surface = Freeze(new SolidColorBrush(Colors.White));
    public static readonly Brush SurfaceAlt = Freeze(new SolidColorBrush(Color.FromRgb(0xF6, 0xF8, 0xFA)));
    public static readonly Brush Danger = Freeze(new SolidColorBrush(Color.FromRgb(0xCF, 0x22, 0x2E)));
    public static readonly FontFamily Font = new("Segoe UI");

    private static ResourceDictionary? _resources;

    /// <summary>Gắn theme vào một cây giao diện (cửa sổ, khung chat...).</summary>
    public static void Apply(FrameworkElement root)
    {
        root.Resources.MergedDictionaries.Add(_resources ??= (ResourceDictionary)XamlReader.Parse(Xaml));
        TextElement.SetFontFamily(root, Font);
        TextElement.SetFontSize(root, 12);
        TextElement.SetForeground(root, Text);
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    // Template viết bằng XAML cho gọn; nạp một lần rồi dùng chung.
    private const string Xaml = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <SolidColorBrush x:Key="Brand" Color="#0B5CAD"/>
          <SolidColorBrush x:Key="BrandHover" Color="#094C8F"/>
          <SolidColorBrush x:Key="Line" Color="#D0D7DE"/>
          <SolidColorBrush x:Key="Hover" Color="#EEF2F6"/>
          <SolidColorBrush x:Key="Text" Color="#1F2328"/>
          <SolidColorBrush x:Key="Muted" Color="#656D76"/>

          <Style TargetType="Button">
            <Setter Property="Background" Value="White"/>
            <Setter Property="Foreground" Value="{StaticResource Text}"/>
            <Setter Property="BorderBrush" Value="{StaticResource Line}"/>
            <Setter Property="Padding" Value="12,4"/>
            <Setter Property="MinHeight" Value="26"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="1" CornerRadius="4" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="Background" Value="{StaticResource Hover}"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="Primary" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
            <Setter Property="Background" Value="{StaticResource Brand}"/>
            <Setter Property="BorderBrush" Value="{StaticResource Brand}"/>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="4" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="Background" Value="{StaticResource BrandHover}"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="Icon" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderBrush" Value="Transparent"/>
            <Setter Property="Foreground" Value="{StaticResource Muted}"/>
            <Setter Property="Padding" Value="6,2"/>
            <Setter Property="MinHeight" Value="22"/>
          </Style>

          <Style TargetType="TextBox">
            <Setter Property="Foreground" Value="{StaticResource Text}"/>
            <Setter Property="BorderBrush" Value="{StaticResource Line}"/>
            <Setter Property="Background" Value="White"/>
            <Setter Property="Padding" Value="6,4"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                  <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4">
                    <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource Brand}"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
            <Setter Property="BorderThickness" Value="1"/>
          </Style>

          <Style TargetType="PasswordBox">
            <Setter Property="BorderBrush" Value="{StaticResource Line}"/>
            <Setter Property="Padding" Value="6,4"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="PasswordBox">
                  <Border x:Name="bd" Background="White" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="4">
                    <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource Brand}"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style TargetType="CheckBox">
            <Setter Property="Foreground" Value="{StaticResource Text}"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="CheckBox">
                  <StackPanel Orientation="Horizontal" Background="Transparent">
                    <Border x:Name="box" Width="15" Height="15" CornerRadius="3" BorderThickness="1" BorderBrush="{StaticResource Line}" Background="White">
                      <TextBlock x:Name="tick" Text="✓" FontSize="11" Foreground="White" HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed"/>
                    </Border>
                    <ContentPresenter Margin="6,0,0,0" VerticalAlignment="Center"/>
                  </StackPanel>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True">
                      <Setter TargetName="box" Property="Background" Value="{StaticResource Brand}"/>
                      <Setter TargetName="box" Property="BorderBrush" Value="{StaticResource Brand}"/>
                      <Setter TargetName="tick" Property="Visibility" Value="Visible"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <ControlTemplate x:Key="ComboToggle" TargetType="ToggleButton">
            <Border x:Name="bd" Background="White" BorderBrush="{StaticResource Line}" BorderThickness="1" CornerRadius="4">
              <TextBlock Text="▾" Foreground="{StaticResource Muted}" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,8,0"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource Brand}"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>

          <Style TargetType="ComboBox">
            <Setter Property="Foreground" Value="{StaticResource Text}"/>
            <Setter Property="MinHeight" Value="26"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ComboBox">
                  <Grid>
                    <ToggleButton Template="{StaticResource ComboToggle}" Focusable="False" ClickMode="Press"
                                  IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"/>
                    <ContentPresenter x:Name="content" IsHitTestVisible="False" Margin="8,0,24,0" VerticalAlignment="Center"
                                      Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
                    <TextBox x:Name="PART_EditableTextBox" Visibility="Hidden" Margin="1,1,22,1" BorderThickness="0" Background="Transparent"
                             Padding="7,3" IsReadOnly="{TemplateBinding IsReadOnly}" VerticalAlignment="Center"/>
                    <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True"
                           Focusable="False" PopupAnimation="None">
                      <Border Background="White" BorderBrush="{StaticResource Line}" BorderThickness="1" CornerRadius="4"
                              MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="320" Margin="0,2,0,0">
                        <ScrollViewer VerticalScrollBarVisibility="Auto"><ItemsPresenter/></ScrollViewer>
                      </Border>
                    </Popup>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsEditable" Value="True">
                      <Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible"/>
                      <Setter TargetName="content" Property="Visibility" Value="Hidden"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style TargetType="ComboBoxItem">
            <Setter Property="Padding" Value="8,4"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ComboBoxItem">
                  <Border x:Name="bd" Background="Transparent" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="bd" Property="Background" Value="{StaticResource Hover}"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style TargetType="ListViewItem">
            <Setter Property="Padding" Value="4,3"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ListViewItem">
                  <Border x:Name="bd" Background="Transparent" Padding="{TemplateBinding Padding}"><GridViewRowPresenter/></Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="Background" Value="#F3F6F9"/></Trigger>
                    <Trigger Property="IsSelected" Value="True"><Setter TargetName="bd" Property="Background" Value="#DCEBFA"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style TargetType="GridViewColumnHeader">
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="GridViewColumnHeader">
                  <Border BorderBrush="{StaticResource Line}" BorderThickness="0,0,0,1" Padding="6,4">
                    <ContentPresenter HorizontalAlignment="Left"/>
                  </Border>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
            <Setter Property="Foreground" Value="{StaticResource Muted}"/>
          </Style>

          <Style TargetType="ListView">
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Background" Value="White"/>
          </Style>

          <Style TargetType="ScrollBar">
            <Setter Property="Width" Value="8"/>
            <Setter Property="MinWidth" Value="8"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ScrollBar">
                  <Track x:Name="PART_Track" IsDirectionReversed="True">
                    <Track.Thumb>
                      <Thumb>
                        <Thumb.Template>
                          <ControlTemplate TargetType="Thumb"><Border Background="#C4CBD2" CornerRadius="4" Margin="1"/></ControlTemplate>
                        </Thumb.Template>
                      </Thumb>
                    </Track.Thumb>
                  </Track>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
            <Style.Triggers>
              <Trigger Property="Orientation" Value="Horizontal">
                <Setter Property="Width" Value="Auto"/>
                <Setter Property="Height" Value="8"/>
                <Setter Property="MinHeight" Value="8"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ScrollBar">
                      <Track x:Name="PART_Track">
                        <Track.Thumb>
                          <Thumb>
                            <Thumb.Template>
                              <ControlTemplate TargetType="Thumb"><Border Background="#C4CBD2" CornerRadius="4" Margin="1"/></ControlTemplate>
                            </Thumb.Template>
                          </Thumb>
                        </Track.Thumb>
                      </Track>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Trigger>
            </Style.Triggers>
          </Style>

          <Style TargetType="DataGrid">
            <Setter Property="Background" Value="White"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="RowBackground" Value="White"/>
            <Setter Property="AlternatingRowBackground" Value="#FAFBFC"/>
            <Setter Property="GridLinesVisibility" Value="Horizontal"/>
            <Setter Property="HorizontalGridLinesBrush" Value="#EEF1F4"/>
            <Setter Property="HeadersVisibility" Value="Column"/>
            <Setter Property="RowHeight" Value="24"/>
            <Setter Property="CanUserAddRows" Value="False"/>
            <Setter Property="CanUserDeleteRows" Value="False"/>
            <Setter Property="IsReadOnly" Value="True"/>
            <Setter Property="SelectionUnit" Value="FullRow"/>
            <Setter Property="ClipboardCopyMode" Value="IncludeHeader"/>
            <Setter Property="FontSize" Value="12"/>
          </Style>
          <Style TargetType="DataGridColumnHeader">
            <Setter Property="Background" Value="White"/>
            <Setter Property="Foreground" Value="{StaticResource Muted}"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Padding" Value="8,5"/>
            <Setter Property="BorderBrush" Value="{StaticResource Line}"/>
            <Setter Property="BorderThickness" Value="0,0,0,1"/>
            <Setter Property="Cursor" Value="Hand"/>
          </Style>
          <Style TargetType="DataGridCell">
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Padding" Value="8,0"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="DataGridCell">
                  <Border Background="Transparent" Padding="{TemplateBinding Padding}">
                    <ContentPresenter VerticalAlignment="Center"/>
                  </Border>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
            <Style.Triggers>
              <Trigger Property="IsSelected" Value="True"><Setter Property="Foreground" Value="{StaticResource Text}"/></Trigger>
            </Style.Triggers>
          </Style>
          <Style TargetType="DataGridRow">
            <Style.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="#F1F5F9"/></Trigger>
              <Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="#DCEBFA"/></Trigger>
            </Style.Triggers>
          </Style>

          <Style TargetType="ToolTip">
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ToolTip">
                  <Border Background="#1F2328" CornerRadius="4" Padding="8,5" MaxWidth="360">
                    <ContentPresenter TextElement.Foreground="White" TextElement.FontSize="11">
                      <ContentPresenter.Resources>
                        <Style TargetType="TextBlock"><Setter Property="TextWrapping" Value="Wrap"/></Style>
                      </ContentPresenter.Resources>
                    </ContentPresenter>
                  </Border>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
        </ResourceDictionary>
        """;
}
