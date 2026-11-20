using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MirrorFlux.ViewModels;

/// <summary>
/// プロパティ変更を画面へ通知するビューモデルの基底クラス。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 値が変わったときだけフィールドを更新し、変更を通知する。
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        // 同じ値なら何もしない。
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        // 値を更新して変更を通知する。
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    /// <summary>
    /// 指定したプロパティの変更を通知する。
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
