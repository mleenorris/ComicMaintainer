using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using ComicMaintainer.MauiApp.Models;
using ComicMaintainer.MauiApp.Services;
using ComicMaintainer.MauiApp.ViewModels;

namespace ComicMaintainer.Tests.AndroidApp;

public sealed class LoginViewModelTests
{
    private readonly FakeSession _session = new();
    private readonly InMemorySettings _settings = new() { ServerUrl = "https://comics.example.test/" };
    private readonly RecordingNavigator _navigator = new();
    private readonly FakeApi _api = new();

    private LoginViewModel Create() => new(_session, _settings, _navigator, _api);

    [Theory]
    [InlineData(true, "✓ Reached ComicMaintainer 3.0.0.")]
    [InlineData(false, "✗ Reached ComicMaintainer 3.0.0.")]
    public async Task TheConnectionTest_WorksBeforeSigningIn(bool success, string expected)
    {
        _api.ConnectionResult = new ConnectionTestResult(success, "Reached ComicMaintainer 3.0.0.");
        var viewModel = Create();
        viewModel.ServerUrl = "https://a.test";

        await viewModel.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal("https://a.test", Assert.Single(_api.TestedAddresses));
        Assert.Equal(expected, viewModel.ConnectionResult);
        Assert.Empty(_navigator.Pages);
    }

    [Fact]
    public async Task TheConnectionTest_SaysSoWhileItRuns()
    {
        _api.HeldConnectionTest = new TaskCompletionSource<ConnectionTestResult>();
        var viewModel = Create();

        var test = viewModel.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsTesting);
        Assert.Equal("Testing connection...", viewModel.ConnectionResult);

        _api.HeldConnectionTest.SetResult(_api.ConnectionResult);
        await test;

        Assert.False(viewModel.IsTesting);
    }

    [Fact]
    public async Task ChangingTheAddress_ClearsTheConnectionTestsResult()
    {
        var viewModel = Create();
        viewModel.ServerUrl = "https://a.test";
        await viewModel.TestConnectionCommand.ExecuteAsync(null);

        viewModel.ServerUrl = "https://b.test";

        Assert.Empty(viewModel.ConnectionResult);
    }

    [Fact]
    public async Task SigningIn_ClearsTheConnectionTestsResult()
    {
        var viewModel = Create();
        await viewModel.TestConnectionCommand.ExecuteAsync(null);
        viewModel.Username = "alice";
        viewModel.Password = "pw";

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.ConnectionResult);
    }

    [Fact]
    public async Task AFailedSignIn_ShowsWhyAndStaysOnThePage()
    {
        _session.SignInFailure = new ApiException(ApiErrorKind.Unauthorized, "Invalid username or password");
        var viewModel = Create();
        viewModel.ServerUrl = "https://a.test";
        viewModel.Username = "alice";
        viewModel.Password = "wrong";

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Equal("Invalid username or password", viewModel.StatusMessage);
        Assert.Empty(_navigator.Pages);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task ARateLimitedSignIn_ShowsHowLongToWait()
    {
        _session.SignInFailure = new ApiException(ApiErrorKind.RateLimited, "Too many requests. Please try again in 5 minutes.");
        var viewModel = Create();
        viewModel.Username = "alice";
        viewModel.Password = "pw";

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Equal("Too many requests. Please try again in 5 minutes.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ASuccessfulSignIn_OpensTheLibraryAndForgetsThePassword()
    {
        var viewModel = Create();
        viewModel.ServerUrl = "https://a.test";
        viewModel.Username = "  alice ";
        viewModel.Password = "pw";
        viewModel.StatusMessage = "an earlier failure";

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Equal(("https://a.test", "alice", "pw"), _session.PasswordSignIn);
        Assert.Equal(["library"], _navigator.Pages);
        Assert.Empty(viewModel.Password);
        Assert.Empty(viewModel.StatusMessage);
    }

    [Fact]
    public async Task AnApiKeySignIn_UsesTheKeyAndForgetsIt()
    {
        var viewModel = Create();
        viewModel.ServerUrl = "https://a.test";
        viewModel.UseApiKey = true;
        viewModel.ApiKey = "cm_key";

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Equal(("https://a.test", "cm_key"), _session.ApiKeySignIn);
        Assert.Null(_session.PasswordSignIn);
        Assert.Empty(viewModel.ApiKey);
    }

    [Theory]
    [InlineData(false, "", "pw")]
    [InlineData(false, "   ", "pw")]
    [InlineData(false, "alice", "")]
    public async Task MissingPasswordFields_AreAskedForWithoutARequest(bool useApiKey, string username, string password)
    {
        var viewModel = Create();
        viewModel.UseApiKey = useApiKey;
        viewModel.Username = username;
        viewModel.Password = password;

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Null(_session.PasswordSignIn);
        Assert.Equal("Enter your username and password.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task AMissingApiKey_IsAskedForWithoutARequest()
    {
        var viewModel = Create();
        viewModel.UseApiKey = true;

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Null(_session.ApiKeySignIn);
        Assert.Equal("Enter your API key.", viewModel.StatusMessage);
    }

    [Fact]
    public void ASessionThatEnds_ShowsWhyOnTheSignInPage()
    {
        var viewModel = Create();

        _session.EndSession("Your session has ended.");

        Assert.Equal("Your session has ended.", viewModel.StatusMessage);
        Assert.Equal(["sign-in"], _navigator.Pages);
    }

    [Fact]
    public void AnOrdinarySignOut_ReturnsToTheSignInPageWithoutAMessage()
    {
        var viewModel = Create();
        viewModel.StatusMessage = "something old";

        _session.EndSession(null);

        Assert.Empty(viewModel.StatusMessage);
        Assert.Equal(["sign-in"], _navigator.Pages);
    }

    [Theory]
    [InlineData(SessionRestoreResult.SignedIn, true)]
    [InlineData(SessionRestoreResult.Unverified, true)]
    [InlineData(SessionRestoreResult.SignedOut, false)]
    public async Task AtStartup_AnEarlierSignInOpensTheLibrary(SessionRestoreResult restored, bool opensLibrary)
    {
        _session.RestoreResult = restored;
        var viewModel = Create();

        await viewModel.InitializeAsync();

        Assert.Equal(_settings.ServerUrl, viewModel.ServerUrl);
        Assert.Equal(opensLibrary ? ["library"] : [], _navigator.Pages);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public void AnUnencryptedAddress_IsWarnedAbout()
    {
        var viewModel = Create();
        var notified = new List<string?>();
        viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        viewModel.ServerUrl = "http://192.168.1.100:5000";

        Assert.Equal(ServerAddress.CleartextWarning, viewModel.ServerWarning);
        Assert.Contains(nameof(LoginViewModel.ServerWarning), notified);
    }
}

public sealed class FilesViewModelTests
{
    private static readonly ComicFile First = new() { FilePath = "/w/a.cbz", FileName = "a.cbz" };
    private static readonly ComicFile Second = new() { FilePath = "/w/b.cbz", FileName = "b.cbz" };

    private readonly FakeApi _api = new();
    private readonly FakeSession _session = new();
    private readonly RecordingDialogs _dialogs = new();

    private FilesViewModel Create() => new(_api, _session, _dialogs);

    private static FilesPage PageOf(int page, int totalPages, params ComicFile[] files) =>
        new() { Page = page, TotalPages = totalPages, TotalFiles = files.Length, Files = [.. files] };

    // Every item the picker really offers: until 3.0.0 the app sent values the server silently ignored.
    public static TheoryData<string, string?> PickerItems()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";
        var items = XDocument.Load(MainPagePath).Descendants(x + "String").Select(element => element.Value).ToArray();
        Assert.Equal(4, items.Length);

        var data = new TheoryData<string, string?>();
        data.Add(items.Single(item => item.EndsWith("All", StringComparison.Ordinal)), null);
        data.Add(items.Single(item => item.EndsWith("Unmarked", StringComparison.Ordinal)), "unmarked");
        data.Add(items.Single(item => item.EndsWith(" Marked", StringComparison.Ordinal)), "marked");
        data.Add(items.Single(item => item.EndsWith("Duplicates", StringComparison.Ordinal)), "duplicates");
        return data;
    }

    private static string MainPagePath => Path.Join(FindRepositoryRoot(), "src", "ComicMaintainer.MauiApp", "Views", "MainPage.xaml");

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Join(current.FullName, "ComicMaintainer.sln")))
        {
            current = current.Parent;
        }

        return Assert.IsType<DirectoryInfo>(current).FullName;
    }

    // RefreshView runs its Command whenever IsRefreshing turns true, from code as well as from the gesture. This does
    // the same with the properties MainPage.xaml really binds, because that is a behaviour of the pair: neither the
    // view model nor the markup is wrong alone.
    private static void ActLikeTheRefreshViewInMainPage(FilesViewModel viewModel)
    {
        var refreshView = XDocument.Load(MainPagePath).Descendants().Single(element => element.Name.LocalName == "RefreshView");
        var isRefreshing = BoundProperty(refreshView, "IsRefreshing");
        var command = Assert.IsAssignableFrom<ICommand>(BoundProperty(refreshView, "Command").GetValue(viewModel));

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == isRefreshing.Name && Assert.IsType<bool>(isRefreshing.GetValue(viewModel)) && command.CanExecute(null))
            {
                command.Execute(null);
            }
        };
    }

    private static PropertyInfo BoundProperty(XElement element, string attribute)
    {
        var path = Regex.Match(element.Attribute(attribute)!.Value, @"^\{Binding (?<path>\w+)\}$").Groups["path"].Value;
        return Assert.IsAssignableFrom<PropertyInfo>(typeof(FilesViewModel).GetProperty(path));
    }

    [Fact]
    public async Task InMainPage_TheNextPageButtonLeavesTheFirstPage()
    {
        _api.Page = PageOf(1, 3, First);
        var viewModel = Create();
        ActLikeTheRefreshViewInMainPage(viewModel);
        await viewModel.LoadFilesCommand.ExecuteAsync(null);
        Assert.Equal(1, Assert.Single(_api.Loads).Page);
        _api.Loads.Clear();

        _api.Page = PageOf(2, 3, Second);
        await viewModel.NextPageCommand.ExecuteAsync(null);

        Assert.Equal(2, Assert.Single(_api.Loads).Page);
        Assert.Equal("Page 2 of 3", viewModel.PageText);
        Assert.Equal([Second], viewModel.Files);
    }

    [Fact]
    public async Task InMainPage_AFailedLoadIsSentAndReportedOnce()
    {
        _api.Failure = new ApiException(ApiErrorKind.Network, "Could not reach the server.");
        var viewModel = Create();
        ActLikeTheRefreshViewInMainPage(viewModel);

        await viewModel.LoadFilesCommand.ExecuteAsync(null);

        Assert.Single(_api.Loads);
        Assert.Single(_dialogs.Alerts);
    }

    [Fact]
    public void InMainPage_PullingToRefreshReloadsTheFirstPageAndStopsTheSpinner()
    {
        _api.Page = PageOf(1, 3, First);
        var viewModel = Create();
        ActLikeTheRefreshViewInMainPage(viewModel);
        viewModel.CurrentPage = 2;

        viewModel.IsRefreshing = true;

        Assert.Equal(1, Assert.Single(_api.Loads).Page);
        Assert.Equal([First], viewModel.Files);
        Assert.False(viewModel.IsRefreshing);
    }

    [Fact]
    public async Task AnOrdinaryLoad_ShowsTheListsSpinnerUntilItIsDone()
    {
        var answer = new TaskCompletionSource<FilesPage>();
        _api.HeldLoads.Enqueue(answer);
        var viewModel = Create();
        var notified = new List<string?>();
        viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        var load = viewModel.LoadFilesCommand.ExecuteAsync(null);

        Assert.True(viewModel.ShowsLoadingIndicator);
        Assert.Contains(nameof(FilesViewModel.ShowsLoadingIndicator), notified);

        answer.SetResult(PageOf(1, 1));
        await load;

        Assert.False(viewModel.ShowsLoadingIndicator);
    }

    [Fact]
    public async Task APullToRefresh_ShowsOnlyRefreshViewsSpinner()
    {
        var answer = new TaskCompletionSource<FilesPage>();
        _api.HeldLoads.Enqueue(answer);
        var viewModel = Create();

        viewModel.IsRefreshing = true;
        var refresh = viewModel.RefreshFilesCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsLoading);
        Assert.False(viewModel.ShowsLoadingIndicator);

        answer.SetResult(PageOf(1, 1));
        await refresh;

        Assert.False(viewModel.IsRefreshing);
    }

    [Fact]
    public async Task AFailedPullToRefresh_StillStopsTheSpinner()
    {
        _api.Failure = new ApiException(ApiErrorKind.Network, "Could not reach the server.");
        var viewModel = Create();
        viewModel.IsRefreshing = true;

        await viewModel.RefreshFilesCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsRefreshing);
    }

    [Fact]
    public async Task AReload_KeepsTheSelectionOnTheFilesStillListed()
    {
        _api.Page = PageOf(1, 1, First, Second);
        var viewModel = Create();
        await viewModel.LoadFilesCommand.ExecuteAsync(null);
        viewModel.SelectedFiles.Add(First);
        viewModel.SelectedFiles.Add(Second);

        // Every load brings new objects, and CollectionView highlights a row by its object.
        var firstAgain = new ComicFile { FilePath = First.FilePath, FileName = First.FileName };
        _api.Page = PageOf(1, 1, firstAgain);
        await viewModel.LoadFilesCommand.ExecuteAsync(null);

        Assert.Same(firstAgain, Assert.Single(viewModel.SelectedFiles));
        Assert.Equal("1 file(s) selected", viewModel.SelectionStatusText);
    }

    [Fact]
    public async Task ChangingPage_DropsASelectionTheUserCanNoLongerSee()
    {
        _api.Page = PageOf(1, 2, First);
        var viewModel = Create();
        await viewModel.LoadFilesCommand.ExecuteAsync(null);
        viewModel.SelectedFiles.Add(First);

        _api.Page = PageOf(2, 2, Second);
        await viewModel.NextPageCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.SelectedFiles);
        Assert.False(viewModel.HasSelectedFiles);
        Assert.Equal("No files selected", viewModel.SelectionStatusText);
    }

    [Fact]
    public async Task AFailedReload_LeavesTheListAndItsSelectionAlone()
    {
        _api.Page = PageOf(1, 1, First);
        var viewModel = Create();
        await viewModel.LoadFilesCommand.ExecuteAsync(null);
        viewModel.SelectedFiles.Add(First);

        _api.Failure = new ApiException(ApiErrorKind.Network, "Could not reach the server.");
        await viewModel.LoadFilesCommand.ExecuteAsync(null);

        Assert.Equal([First], viewModel.Files);
        Assert.Same(First, Assert.Single(viewModel.SelectedFiles));
    }

    [Fact]
    public async Task AnAnswerArrivingAfterTheSessionEnded_IsDiscarded()
    {
        var answer = new TaskCompletionSource<FilesPage>();
        _api.HeldLoads.Enqueue(answer);
        var viewModel = Create();
        var load = viewModel.LoadFilesCommand.ExecuteAsync(null);

        _session.EndSession(null);
        Assert.False(viewModel.IsLoading);

        answer.SetResult(PageOf(2, 2, First));
        await load;

        Assert.Empty(viewModel.Files);
        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal(1, viewModel.TotalPages);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task AFailureArrivingAfterTheSessionEnded_IsNotShown()
    {
        var answer = new TaskCompletionSource<FilesPage>();
        _api.HeldLoads.Enqueue(answer);
        var viewModel = Create();
        var load = viewModel.LoadFilesCommand.ExecuteAsync(null);

        _session.EndSession(null);
        answer.SetException(new ApiException(ApiErrorKind.Network, "Could not reach the server."));
        await load;

        Assert.Empty(_dialogs.Alerts);
    }

    [Fact]
    public async Task AnOlderLoadArrivingLate_CannotReplaceANewerOnesAnswer()
    {
        var older = new TaskCompletionSource<FilesPage>();
        var newer = new TaskCompletionSource<FilesPage>();
        _api.HeldLoads.Enqueue(older);
        _api.HeldLoads.Enqueue(newer);
        var viewModel = Create();
        var olderLoad = viewModel.LoadFilesCommand.ExecuteAsync(null);
        var newerLoad = viewModel.RefreshFilesCommand.ExecuteAsync(null);

        newer.SetResult(PageOf(1, 1, Second));
        await newerLoad;
        older.SetResult(PageOf(1, 1, First));
        await olderLoad;

        Assert.Equal([Second], viewModel.Files);
    }

    [Fact]
    public async Task TheSpinner_StaysUntilTheNewestLoadIsDone()
    {
        var older = new TaskCompletionSource<FilesPage>();
        var newer = new TaskCompletionSource<FilesPage>();
        _api.HeldLoads.Enqueue(older);
        _api.HeldLoads.Enqueue(newer);
        var viewModel = Create();
        var olderLoad = viewModel.LoadFilesCommand.ExecuteAsync(null);
        var newerLoad = viewModel.RefreshFilesCommand.ExecuteAsync(null);

        older.SetResult(PageOf(1, 1, First));
        await olderLoad;
        Assert.True(viewModel.IsLoading);

        newer.SetResult(PageOf(1, 1, Second));
        await newerLoad;
        Assert.False(viewModel.IsLoading);
    }

    [Theory]
    [MemberData(nameof(PickerItems))]
    public void EachPickerItem_MapsToAFilterTheServerKnows(string item, string? expected)
    {
        Assert.Equal(expected, FilesViewModel.ToApiFilter(item));
    }

    [Fact]
    public async Task Loading_ShowsThePageAndSendsTheFilterAndSearch()
    {
        _api.Page = PageOf(1, 3, First, Second);
        var viewModel = Create();
        viewModel.SelectedFilter = "⚠️ Unmarked";
        _api.Loads.Clear();

        await viewModel.LoadFilesCommand.ExecuteAsync(null);

        Assert.Equal([First, Second], viewModel.Files);
        Assert.Equal((1, "unmarked", (string?)null), Assert.Single(_api.Loads));
        Assert.Equal("Page 1 of 3", viewModel.PageText);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task APageTheServerClamped_IsWhatIsShown()
    {
        _api.Page = PageOf(2, 2, First);
        var viewModel = Create();
        viewModel.CurrentPage = 9;

        await viewModel.LoadFilesCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.CurrentPage);
        Assert.False(viewModel.HasNextPage);
    }

    [Fact]
    public async Task AnEmptyLibrary_StillCountsAsOnePage()
    {
        _api.Page = PageOf(1, 0);
        var viewModel = Create();

        await viewModel.LoadFilesCommand.ExecuteAsync(null);

        Assert.Equal(1, viewModel.TotalPages);
    }

    [Fact]
    public async Task Paging_StopsAtBothEnds()
    {
        _api.Page = PageOf(1, 2, First);
        var viewModel = Create();
        await viewModel.LoadFilesCommand.ExecuteAsync(null);
        _api.Loads.Clear();

        await viewModel.PreviousPageCommand.ExecuteAsync(null);
        Assert.Empty(_api.Loads);
        Assert.False(viewModel.HasPreviousPage);

        _api.Page = PageOf(2, 2, Second);
        await viewModel.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(2, Assert.Single(_api.Loads).Page);
        Assert.True(viewModel.HasPreviousPage);

        await viewModel.NextPageCommand.ExecuteAsync(null);
        Assert.Single(_api.Loads);

        _api.Page = PageOf(1, 2, First);
        await viewModel.PreviousPageCommand.ExecuteAsync(null);
        Assert.Equal(1, _api.Loads[^1].Page);
    }

    [Fact]
    public async Task Refreshing_GoesBackToTheFirstPage()
    {
        var viewModel = Create();
        viewModel.CurrentPage = 4;

        await viewModel.RefreshFilesCommand.ExecuteAsync(null);

        Assert.Equal(1, Assert.Single(_api.Loads).Page);
    }

    [Fact]
    public async Task ChangingTheFilter_GoesBackToTheFirstPage()
    {
        var viewModel = Create();
        viewModel.CurrentPage = 4;

        viewModel.SelectedFilter = "🔁 Duplicates";
        await Task.Yield();

        Assert.Equal((1, "duplicates", (string?)null), Assert.Single(_api.Loads));
    }

    [Fact]
    public async Task AFailureToLoad_IsShown()
    {
        _api.Failure = new ApiException(ApiErrorKind.Network, "Could not reach the server.");
        var viewModel = Create();

        await viewModel.LoadFilesCommand.ExecuteAsync(null);

        Assert.Equal("Could not reach the server.", Assert.Single(_dialogs.Alerts));
        Assert.False(viewModel.IsLoading);
    }

    // The sign-in page explains a session that ended; an alert on top of it would only get in the way.
    [Fact]
    public async Task AnEndedSession_RaisesNoAlertOfItsOwn()
    {
        _api.Failure = new ApiException(ApiErrorKind.Unauthorized, "Authentication required");
        var viewModel = Create();

        await viewModel.LoadFilesCommand.ExecuteAsync(null);

        Assert.Empty(_dialogs.Alerts);
    }

    [Fact]
    public async Task ProcessingAFile_ConfirmsAndReloads()
    {
        var viewModel = Create();

        await viewModel.ProcessFileCommand.ExecuteAsync(First);

        Assert.Equal(["/w/a.cbz"], _api.Processed);
        Assert.Equal("File processed successfully", Assert.Single(_dialogs.Alerts));
        Assert.Single(_api.Loads);
    }

    [Fact]
    public async Task AFileThatCannotBeProcessed_ShowsTheServersReason()
    {
        _api.Failure = new ApiException(ApiErrorKind.Forbidden, "Insufficient permissions");
        var viewModel = Create();

        await viewModel.ProcessFileCommand.ExecuteAsync(First);

        Assert.Equal("Insufficient permissions", Assert.Single(_dialogs.Alerts));
    }

    [Fact]
    public async Task DeletingAFile_AsksFirst()
    {
        _dialogs.ConfirmAnswer = false;
        var viewModel = Create();

        await viewModel.DeleteFileCommand.ExecuteAsync(First);

        Assert.Empty(_api.Deleted);
    }

    [Fact]
    public async Task DeletingAFile_CallsTheServerAndReloads()
    {
        var viewModel = Create();

        await viewModel.DeleteFileCommand.ExecuteAsync(First);

        Assert.Equal(["/w/a.cbz"], _api.Deleted);
        Assert.Single(_api.Loads);
    }

    [Fact]
    public async Task AFileThatCannotBeDeleted_ShowsTheReason()
    {
        _api.Failure = new ApiException(ApiErrorKind.NotFound, "The server could not find that.");
        var viewModel = Create();

        await viewModel.DeleteFileCommand.ExecuteAsync(First);

        Assert.Equal("The server could not find that.", Assert.Single(_dialogs.Alerts));
    }

    [Fact]
    public void Selecting_UpdatesTheStatus()
    {
        var viewModel = Create();

        viewModel.SelectedFiles.Add(First);

        Assert.True(viewModel.HasSelectedFiles);
        Assert.Equal("1 file(s) selected", viewModel.SelectionStatusText);

        viewModel.SelectedFiles.Clear();

        Assert.False(viewModel.HasSelectedFiles);
        Assert.Equal("No files selected", viewModel.SelectionStatusText);
    }

    [Fact]
    public async Task ProcessingTheSelection_SendsItsPaths()
    {
        var viewModel = Create();
        viewModel.SelectedFiles.Add(First);
        viewModel.SelectedFiles.Add(Second);

        await viewModel.ProcessSelectedFilesCommand.ExecuteAsync(null);

        Assert.Equal(["/w/a.cbz", "/w/b.cbz"], Assert.Single(_api.BatchProcessed));
        Assert.Empty(viewModel.SelectedFiles);
        Assert.Equal("Processing 2 file(s) on the server.", Assert.Single(_dialogs.Alerts));
    }

    [Fact]
    public async Task DeletingTheSelection_SendsItsPaths()
    {
        var viewModel = Create();
        viewModel.SelectedFiles.Add(First);

        await viewModel.DeleteSelectedFilesCommand.ExecuteAsync(null);

        Assert.Equal(["/w/a.cbz"], Assert.Single(_api.BatchDeleted));
        Assert.Empty(viewModel.SelectedFiles);
    }

    [Fact]
    public async Task TheSelectionCommands_WithNothingSelected_SayAndDoNothing()
    {
        var viewModel = Create();

        await viewModel.ProcessSelectedFilesCommand.ExecuteAsync(null);
        await viewModel.DeleteSelectedFilesCommand.ExecuteAsync(null);

        Assert.Equal(["No files selected", "No files selected"], _dialogs.Alerts);
        Assert.Empty(_api.BatchProcessed);
        Assert.Empty(_api.BatchDeleted);
    }

    [Fact]
    public async Task TheSelectionCommands_DeclinedDoNothing()
    {
        _dialogs.ConfirmAnswer = false;
        var viewModel = Create();
        viewModel.SelectedFiles.Add(First);

        await viewModel.ProcessSelectedFilesCommand.ExecuteAsync(null);
        await viewModel.DeleteSelectedFilesCommand.ExecuteAsync(null);

        Assert.Empty(_api.BatchProcessed);
        Assert.Empty(_api.BatchDeleted);
        Assert.Single(viewModel.SelectedFiles);
    }

    [Fact]
    public async Task ASelectionThatCannotBeActedOn_StaysSelectedAndShowsTheReason()
    {
        _api.Failure = new ApiException(ApiErrorKind.Rejected, "No files specified");
        var viewModel = Create();
        viewModel.SelectedFiles.Add(First);

        await viewModel.ProcessSelectedFilesCommand.ExecuteAsync(null);
        await viewModel.DeleteSelectedFilesCommand.ExecuteAsync(null);

        Assert.Equal(["No files specified", "No files specified"], _dialogs.Alerts);
        Assert.Single(viewModel.SelectedFiles);
    }

    [Theory]
    [InlineData("🚀 Process", 1, 0, 1)]
    [InlineData("🗑️ Delete", 0, 1, 0)]
    [InlineData("📝 View Details", 0, 0, 1)]
    [InlineData(null, 0, 0, 0)]
    public async Task TheFileMenu_RunsTheChosenAction(string? action, int processes, int deletes, int alerts)
    {
        _dialogs.ChosenAction = action;
        var viewModel = Create();

        await viewModel.ShowFileActionsCommand.ExecuteAsync(First);

        Assert.Equal(processes, _api.Processed.Count);
        Assert.Equal(deletes, _api.Deleted.Count);
        Assert.Equal(alerts, _dialogs.Alerts.Count);
    }

    [Fact]
    public async Task TheFileDetails_DescribeTheFile()
    {
        _dialogs.ChosenAction = "📝 View Details";
        var viewModel = Create();

        await viewModel.ShowFileActionsCommand.ExecuteAsync(new ComicFile { FilePath = "/w/x/a.cbz", FileName = "a.cbz", IsProcessed = true });

        var details = Assert.Single(_dialogs.Alerts);
        Assert.Contains("Directory: /w/x", details, StringComparison.Ordinal);
        Assert.Contains("Status: Processed", details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheSessionEnds_TheNextAccountSeesNothingOfThisOne()
    {
        _api.Page = PageOf(2, 2, First);
        var viewModel = Create();
        await viewModel.LoadFilesCommand.ExecuteAsync(null);
        viewModel.SelectedFiles.Add(First);

        _session.EndSession(null);

        Assert.Empty(viewModel.Files);
        Assert.Empty(viewModel.SelectedFiles);
        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal(1, viewModel.TotalPages);
    }

    [Fact]
    public void AfterDisposal_SessionEndsAreIgnored()
    {
        using var viewModel = Create();
        viewModel.Files.Add(First);

        viewModel.Dispose();
        _session.EndSession(null);

        Assert.Single(viewModel.Files);
    }

    [Fact]
    public async Task Searching_WaitsForTypingToStopAndStartsAtTheFirstPage()
    {
        var viewModel = Create();
        viewModel.CurrentPage = 3;

        viewModel.SearchText = "ba";
        viewModel.SearchText = "batman";

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (_api.Loads.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Equal((1, (string?)null, "batman"), Assert.Single(_api.Loads));
    }
}

public sealed class SettingsViewModelTests
{
    private const string Current = "https://comics.example.test/";

    private readonly FakeApi _api = new();
    private readonly FakeSession _session = new();
    private readonly InMemorySettings _settings = new() { ServerUrl = Current };
    private readonly RecordingDialogs _dialogs = new();
    private readonly FakeTheme _theme = new();

    private SettingsViewModel Create() => new(_api, _settings, _session, _dialogs, _theme);

    [Theory]
    [InlineData(true, "✓ Reached ComicMaintainer 3.0.0.")]
    [InlineData(false, "✗ Reached ComicMaintainer 3.0.0.")]
    public async Task TheConnectionTest_ReportsItsResult(bool success, string expected)
    {
        _api.ConnectionResult = new ConnectionTestResult(success, "Reached ComicMaintainer 3.0.0.");
        var viewModel = Create();

        await viewModel.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(expected, viewModel.TestResult);
        Assert.False(viewModel.IsTesting);
    }

    [Fact]
    public async Task AnUnusableAddress_IsNotSaved()
    {
        var viewModel = Create();
        viewModel.ServerUrl = "comics.example.test";

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(Current, _settings.ServerUrl);
        Assert.StartsWith("✗ ", viewModel.TestResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingWhileSignedOut_StoresTheNormalizedAddress()
    {
        var viewModel = Create();
        viewModel.ServerUrl = "https://new.example.test";

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal("https://new.example.test/", _settings.ServerUrl);
        Assert.Equal("https://new.example.test/", viewModel.ServerUrl);
        Assert.Equal("✓ Settings saved", viewModel.TestResult);
        Assert.Equal(0, _session.SignOuts);
        Assert.Empty(_dialogs.Questions);
    }

    [Fact]
    public async Task SavingTheSameAddressWhileSignedIn_KeepsTheSession()
    {
        _session.CurrentKind = CredentialKind.Session;
        var viewModel = Create();

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(0, _session.SignOuts);
        Assert.Equal("✓ Settings saved", viewModel.TestResult);
    }

    [Fact]
    public async Task ChangingTheServerWhileSignedIn_SignsOut()
    {
        _session.CurrentKind = CredentialKind.Session;
        var viewModel = Create();
        viewModel.ServerUrl = "https://new.example.test";

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Single(_dialogs.Questions);
        Assert.Equal(1, _session.SignOuts);
        Assert.Equal("https://new.example.test/", _settings.ServerUrl);
    }

    [Fact]
    public async Task DecliningTheServerChange_ChangesNothing()
    {
        _session.CurrentKind = CredentialKind.Session;
        _dialogs.ConfirmAnswer = false;
        var viewModel = Create();
        viewModel.ServerUrl = "https://new.example.test";

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(0, _session.SignOuts);
        Assert.Equal(Current, _settings.ServerUrl);
    }

    [Fact]
    public async Task SigningOut_EndsTheSession()
    {
        await Create().SignOutCommand.ExecuteAsync(null);

        Assert.Equal(1, _session.SignOuts);
    }

    [Fact]
    public async Task SigningOut_ShowsThatItIsBusyUntilItIsDone()
    {
        _session.HeldSignOut = new TaskCompletionSource();
        var viewModel = Create();

        var signOut = viewModel.SignOutCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSigningOut);

        _session.HeldSignOut.SetResult();
        await signOut;

        Assert.False(viewModel.IsSigningOut);
    }

    [Fact]
    public async Task ChangingTheServerWhileSignedIn_ShowsThatItIsBusySigningOut()
    {
        _session.CurrentKind = CredentialKind.Session;
        _session.HeldSignOut = new TaskCompletionSource();
        var viewModel = Create();
        viewModel.ServerUrl = "https://new.example.test";

        var save = viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSigningOut);

        _session.HeldSignOut.SetResult();
        await save;

        Assert.False(viewModel.IsSigningOut);
    }

    [Theory]
    [InlineData("alice", CredentialKind.Session, "Signed in as alice")]
    [InlineData("alice", CredentialKind.ApiKey, "Signed in as alice with an API key")]
    [InlineData(null, CredentialKind.Session, "Signed in")]
    public void TheAccount_IsDescribed(string? username, CredentialKind kind, string expected)
    {
        _session.CurrentKind = kind;
        _session.Account = username is null ? null : new AccountInfo { Username = username };

        Assert.Equal(expected, Create().AccountDescription);
    }

    [Fact]
    public void TheDarkModeSwitch_StartsFromAndChangesTheTheme()
    {
        _theme.IsDarkMode = true;
        var viewModel = Create();
        Assert.True(viewModel.IsDarkMode);

        viewModel.IsDarkMode = false;

        Assert.False(_theme.IsDarkMode);
    }

    [Fact]
    public void AnUnencryptedAddress_IsWarnedAbout()
    {
        var viewModel = Create();

        viewModel.ServerUrl = "http://192.168.1.100:5000";

        Assert.Equal(ServerAddress.CleartextWarning, viewModel.ServerWarning);
    }
}
