namespace v2rayN.Converters;

[ValueConversion(typeof(bool), typeof(bool))]
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (targetType != typeof(bool))
        {
            throw new InvalidOperationException("The target must be a boolean");
        }

        if (value is not bool boolean)
        {
            throw new InvalidOperationException("The value must be a boolean");
        }

        return !boolean;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (targetType != typeof(bool))
        {
            throw new InvalidOperationException("The target must be a boolean");
        }

        if (value is not bool boolean)
        {
            throw new InvalidOperationException("The value must be a boolean");
        }

        return !boolean;
    }
}
