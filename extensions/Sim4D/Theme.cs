using System;
using System.Windows;
using System.Windows.Markup;

namespace BIMTech4D
{
    /// <summary>Giao diện phẳng, bo góc. Viết XAML dạng chuỗi để không phụ thuộc resource biên dịch.</summary>
    public static class Theme
    {
        public static bool TryApply(FrameworkElement target)
        {
            try
            {
                var rd = (ResourceDictionary)XamlReader.Parse(Xaml);
                target.Resources.MergedDictionaries.Add(rd);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("BIMTech4D theme: " + ex);
                return false;
            }
        }

        const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <SolidColorBrush x:Key='Accent' Color='#2563EB'/>
  <SolidColorBrush x:Key='AccentHover' Color='#1D4ED8'/>
  <SolidColorBrush x:Key='Line' Color='#E5E7EB'/>

  <Style TargetType='Button'>
    <Setter Property='Background' Value='White'/>
    <Setter Property='BorderBrush' Value='#D1D5DB'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Foreground' Value='#1F2937'/>
    <Setter Property='Padding' Value='12,6'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                  BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='6' Padding='{TemplateBinding Padding}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#F3F4F6'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Background' Value='#E5E7EB'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Primary' TargetType='Button'>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='Padding' Value='14,8'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Background='{StaticResource Accent}' CornerRadius='6' Padding='{TemplateBinding Padding}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource AccentHover}'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Round' TargetType='Button'>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Grid>
            <Ellipse x:Name='e' Fill='{StaticResource Accent}'/>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='e' Property='Fill' Value='{StaticResource AccentHover}'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='SliderFill' TargetType='RepeatButton'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RepeatButton'>
          <Grid Background='Transparent'><Border Height='4' CornerRadius='2' Background='{StaticResource Accent}'/></Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='SliderEmpty' TargetType='RepeatButton'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RepeatButton'>
          <Grid Background='Transparent'><Border Height='4' CornerRadius='2' Background='#E5E7EB'/></Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='Slider'>
    <Setter Property='IsMoveToPointEnabled' Value='True'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Slider'>
          <Track x:Name='PART_Track' Height='22'>
            <Track.DecreaseRepeatButton><RepeatButton Style='{StaticResource SliderFill}' Command='{x:Static Slider.DecreaseLarge}'/></Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton><RepeatButton Style='{StaticResource SliderEmpty}' Command='{x:Static Slider.IncreaseLarge}'/></Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb Width='16' Height='16'>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'><Ellipse Fill='White' Stroke='{StaticResource Accent}' StrokeThickness='3'/></ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='DataGrid'>
    <Setter Property='BorderThickness' Value='0'/>
    <Setter Property='Background' Value='White'/>
    <Setter Property='RowBackground' Value='White'/>
    <Setter Property='AlternatingRowBackground' Value='White'/>
    <Setter Property='GridLinesVisibility' Value='Horizontal'/>
    <Setter Property='HorizontalGridLinesBrush' Value='#F1F2F4'/>
    <Setter Property='HeadersVisibility' Value='Column'/>
    <Setter Property='RowHeight' Value='30'/>
  </Style>
  <Style TargetType='DataGridColumnHeader'>
    <Setter Property='Background' Value='White'/>
    <Setter Property='Foreground' Value='#6B7280'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='Padding' Value='8,6'/>
    <Setter Property='BorderBrush' Value='#E5E7EB'/>
    <Setter Property='BorderThickness' Value='0,0,0,1'/>
    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='DataGridColumnHeader'>
          <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                  BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}'>
            <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='DataGridRow'>
    <Style.Triggers>
      <Trigger Property='IsMouseOver' Value='True'><Setter Property='Background' Value='#F8FAFC'/></Trigger>
    </Style.Triggers>
  </Style>
  <Style TargetType='DataGridCell'>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='DataGridCell'>
          <Border Background='{TemplateBinding Background}' Padding='8,0'>
            <ContentPresenter VerticalAlignment='Stretch'/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property='IsSelected' Value='True'>
        <Setter Property='Background' Value='#E8F0FE'/>
        <Setter Property='Foreground' Value='#111827'/>
      </Trigger>
    </Style.Triggers>
  </Style>
</ResourceDictionary>";
    }
}
