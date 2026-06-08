// ============================================================
//  ConnectViewModel.cs  —  ตัวอย่าง ViewModel สำหรับหน้า Connect
//  ใช้ MVVM + INotifyPropertyChanged (หรือ CommunityToolkit.Mvvm)
// ============================================================

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace YourWpfApp.ViewModels;

public class ConnectViewModel : INotifyPropertyChanged
{
    // ----------------------------------------------------------
    // Properties
    // ----------------------------------------------------------
    private string _selectedPort = ConDmsLockerCmd.LockerConfig.Instance.Port;
    public string SelectedPort
    {
        get => _selectedPort;
        set { _selectedPort = value; OnPropertyChanged(); }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusColor)); }
    }

    private string _statusMessage = "ยังไม่ได้เชื่อมต่อ";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public string StatusText  => IsConnected ? "เชื่อมต่อแล้ว" : "ไม่ได้เชื่อมต่อ";
    public string StatusColor => IsConnected ? "#1D9E75" : "#D85A30"; // teal / coral

    // Port list (โหลดจาก SerialPort.GetPortNames())
    public ObservableCollection<string> AvailablePorts { get; } = new();

    // Commands
    public ICommand ConnectCommand    { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand RefreshPortsCommand { get; }
    public ICommand TestUnlockCommand { get; }

    // ----------------------------------------------------------
    // Constructor
    // ----------------------------------------------------------
    public ConnectViewModel()
    {
        ConnectCommand     = new RelayCommand(ExecuteConnect,    () => !IsConnected);
        DisconnectCommand  = new RelayCommand(ExecuteDisconnect, () => IsConnected);
        RefreshPortsCommand = new RelayCommand(LoadPorts);
        TestUnlockCommand  = new RelayCommand(ExecuteTestUnlock, () => IsConnected);

        LoadPorts();
    }

    // ----------------------------------------------------------
    // Connect
    // ----------------------------------------------------------
    private void ExecuteConnect()
    {
        StatusMessage = $"กำลังเชื่อมต่อ {SelectedPort}...";

        bool success = LockerService.ConnectPort(SelectedPort);

        if (success)
        {
            IsConnected   = true;
            StatusMessage = $"เชื่อมต่อ {SelectedPort} สำเร็จ";
        }
        else
        {
            IsConnected   = false;
            StatusMessage = $"ไม่สามารถเชื่อมต่อ {SelectedPort} ได้ — ตรวจสอบ port และ adapter";
        }
    }

    // ----------------------------------------------------------
    // Disconnect
    // ----------------------------------------------------------
    private void ExecuteDisconnect()
    {
        LockerService.Disconnect();
        IsConnected   = false;
        StatusMessage = "ตัดการเชื่อมต่อแล้ว";
    }

    // ----------------------------------------------------------
    // Test Unlock (board 1, channel 1)
    // ----------------------------------------------------------
    private void ExecuteTestUnlock()
    {
        string result = LockerService.Unlock(boardAddr: 0x01, lockAddr: 0x01);
        StatusMessage = result == "ok"
            ? "ทดสอบเปิดช่อง 1 สำเร็จ"
            : $"ทดสอบล้มเหลว: {result}";
    }

    // ----------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------
    private void LoadPorts()
    {
        AvailablePorts.Clear();
        foreach (var p in System.IO.Ports.SerialPort.GetPortNames())
            AvailablePorts.Add(p);

        if (AvailablePorts.Count > 0 && !AvailablePorts.Contains(SelectedPort))
            SelectedPort = AvailablePorts[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Minimal RelayCommand (หรือใช้ CommunityToolkit.Mvvm แทน)
public class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged
    {
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
