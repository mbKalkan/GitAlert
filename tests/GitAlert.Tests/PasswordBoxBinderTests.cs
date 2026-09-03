using System.ComponentModel;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using GitAlert.Views;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// A password box cannot expose its content as a bindable property, so GitAlert wires one up by
/// hand. The wiring broke once in a way nothing else could catch: the listener was attached from
/// the property-changed callback, which WPF does not raise when a binding's first value happens to
/// equal the property's default. An empty token field is exactly that case, so typing a token went
/// nowhere and adding an account always answered "paste a token first".
/// </summary>
public class PasswordBoxBinderTests
{
    private sealed class Source : INotifyPropertyChanged
    {
        private string _token = string.Empty;

        public string Token
        {
            get => _token;
            set
            {
                _token = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Token)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Password boxes are STA-only, so the whole exercise runs on its own STA thread.</summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return failure is null ? result : throw failure;
    }

    private static (PasswordBox Box, Source Model) Bind(string initial)
    {
        var model = new Source { Token = initial };
        var box = new PasswordBox();

        BindingOperations.SetBinding(
            box,
            PasswordBoxBinder.BoundPasswordProperty,
            new Binding(nameof(Source.Token)) { Source = model, Mode = BindingMode.TwoWay });

        return (box, model);
    }

    /// <summary>
    /// The regression itself: an empty starting value is what the add-account field always has.
    /// </summary>
    [Fact]
    public void Typing_reaches_the_view_model_even_when_the_field_started_empty()
    {
        var typed = OnStaThread(() =>
        {
            var (box, model) = Bind(string.Empty);
            box.Password = "ghp_typed_by_hand";
            return model.Token;
        });

        Assert.Equal("ghp_typed_by_hand", typed);
    }

    [Fact]
    public void Typing_reaches_the_view_model_when_the_field_started_null()
    {
        var typed = OnStaThread(() =>
        {
            var (box, model) = Bind(null!);
            box.Password = "ghp_replacement";
            return model.Token;
        });

        Assert.Equal("ghp_replacement", typed);
    }

    [Fact]
    public void Clearing_the_view_model_clears_the_box()
    {
        var password = OnStaThread(() =>
        {
            var (box, model) = Bind(string.Empty);
            box.Password = "ghp_typed_by_hand";

            // What Cancel does: the view model drops the value and the box has to follow.
            model.Token = string.Empty;
            return box.Password;
        });

        Assert.Equal(string.Empty, password);
    }

    [Fact]
    public void A_password_box_nobody_bound_is_left_alone()
    {
        var value = OnStaThread(() =>
        {
            var box = new PasswordBox { Password = "not bound to anything" };
            return PasswordBoxBinder.GetBoundPassword(box);
        });

        Assert.Equal(string.Empty, value);
    }
}
