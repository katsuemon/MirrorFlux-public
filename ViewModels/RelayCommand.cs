using System.Windows.Input;

namespace MirrorFlux.ViewModels;

/// <summary>
/// 処理と実行可否の判定を渡して作る、引数付きの汎用コマンド。
/// </summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Predicate<T?>? _canExecute;

    /// <summary>
    /// 実行する処理と、省略可能な実行可否の判定を受け取る。
    /// </summary>
    public RelayCommand(Action<T?> execute, Predicate<T?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke((T?)parameter) ?? true;

    /// <summary>
    /// 引数を型に合わせて変換し、処理を実行する。
    /// </summary>
    public void Execute(object? parameter)
    {
        // 値型で引数がないときは既定値で実行する。
        if (parameter is null && typeof(T).IsValueType)
        {
            _execute(default);
            return;
        }

        // XAMLから文字列で渡された数値を int に変換する。
        if (parameter is string str && typeof(T) == typeof(int) && int.TryParse(str, out var intVal))
        {
            _execute((T)(object)intVal);
            return;
        }

        // それ以外はそのまま型変換して渡す。
        _execute((T?)parameter);
    }

    /// <summary>
    /// 実行可否の再評価を WPF の CommandManager に任せる。
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
