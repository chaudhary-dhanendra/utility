using System.Windows;
using System.Windows.Controls;

namespace MigrationStudio.Desktop.Behaviors;

public static class PasswordBoxBinding
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password",
        typeof(string),
        typeof(PasswordBoxBinding),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    public static void SetPassword(DependencyObject element, string value) => element.SetValue(PasswordProperty, value);

    public static string GetPassword(DependencyObject element) => (string)element.GetValue(PasswordProperty);

    private static void OnPasswordChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not PasswordBox passwordBox)
        {
            return;
        }
        passwordBox.PasswordChanged -= OnPasswordBoxChanged;
        passwordBox.Password = args.NewValue as string ?? string.Empty;
        passwordBox.PasswordChanged += OnPasswordBoxChanged;
    }

    private static void OnPasswordBoxChanged(object sender, RoutedEventArgs args)
    {
        var passwordBox = (PasswordBox)sender;
        SetPassword(passwordBox, passwordBox.Password);
    }
}
