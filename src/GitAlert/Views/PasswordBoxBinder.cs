using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace GitAlert.Views;

/// <summary>
/// Lets a <see cref="PasswordBox"/> take part in data binding. The control deliberately refuses to
/// expose its content as a bindable dependency property, which is fine for a single hand-wired
/// field but not for password boxes that live inside an items template - one per account here.
/// </summary>
public static class PasswordBoxBinder
{
    /// <summary>
    /// Listening for typing is set up once for every password box in the application, rather than
    /// from the property-changed callback below.
    /// </summary>
    /// <remarks>
    /// Subscribing from that callback looks natural and is subtly broken: WPF raises it only when
    /// the value actually changes, so a binding whose source already holds the property's default
    /// - an empty string, which is exactly what an empty token field starts as - pushes a value
    /// equal to the default, raises nothing, and leaves the box with no listener attached. Typing
    /// then went nowhere and adding an account could only ever answer "paste a token first".
    /// </remarks>
    static PasswordBoxBinder() =>
        EventManager.RegisterClassHandler(
            typeof(PasswordBox),
            PasswordBox.PasswordChangedEvent,
            new RoutedEventHandler(OnPasswordChanged));

    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxBinder),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBoundPasswordChanged));

    /// <summary>Guards against the box and the property updating each other in a loop.</summary>
    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached(
            "IsUpdating",
            typeof(bool),
            typeof(PasswordBoxBinder),
            new PropertyMetadata(false));

    public static string GetBoundPassword(DependencyObject element) =>
        (string)element.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject element, string value) =>
        element.SetValue(BoundPasswordProperty, value);

    /// <summary>The view model changed the value; push it into the box.</summary>
    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box || (bool)box.GetValue(IsUpdatingProperty))
        {
            return;
        }

        var value = e.NewValue as string ?? string.Empty;

        if (box.Password != value)
        {
            box.Password = value;
        }
    }

    /// <summary>Someone typed; push it back out to the view model.</summary>
    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;

        // The class handler sees every password box, including any that are not bound at all.
        if (BindingOperations.GetBindingExpression(box, BoundPasswordProperty) is null)
        {
            return;
        }

        box.SetValue(IsUpdatingProperty, true);

        try
        {
            SetBoundPassword(box, box.Password);
        }
        finally
        {
            box.SetValue(IsUpdatingProperty, false);
        }
    }
}
