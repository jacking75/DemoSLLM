using CommunityToolkit.Mvvm.ComponentModel;
using LocalMind.Core;

namespace LocalMind.App;

// UI framework independent; services and state remain reusable with another shell.
public sealed class ShellViewModel(IInferenceBackend backend) : ObservableObject
{
    private string status = "서버를 시작하면 로컬 채팅을 사용할 수 있다.";
    public string Status { get => status; set => SetProperty(ref status, value); }
    private bool busy;
    public bool Busy { get => busy; set => SetProperty(ref busy, value); }
    public IInferenceBackend Backend { get; } = backend;
}
