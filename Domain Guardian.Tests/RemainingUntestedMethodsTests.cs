// Unit tests for remaining untested methods:
// ReplaceCollection, GetActiveFindings, ApplyFindingsFilter,
// SyncFindingItems, SyncResultItems, ForceRefreshDashboard

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using AdHealthMonitor;
using Xunit;

namespace Domain_Guardian.Tests
{
    public class RemainingUntestedMethodsTests
    {
        // ── ReplaceCollection tests (private static generic method) ──────────

        private static void InvokeReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
        {
            var method = typeof(MainWindow).GetMethod(
                "ReplaceCollection",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var genericMethod = method!.MakeGenericMethod(typeof(T));
            genericMethod.Invoke(null, new object[] { target, source });
        }

        [Fact]
        public void ReplaceCollection_SourceMatchesTarget_NoChanges()
        {
            var target = new ObservableCollection<string> { "a", "b", "c" };
            var source = new List<string> { "a", "b", "c" };

            InvokeReplaceCollection(target, source);

            Assert.Equal(3, target.Count);
            Assert.Equal("a", target[0]);
            Assert.Equal("b", target[1]);
            Assert.Equal("c", target[2]);
        }

        [Fact]
        public void ReplaceCollection_SourceFewerItems_ExtraRemoved()
        {
            var target = new ObservableCollection<string> { "a", "b", "c", "d" };
            var source = new List<string> { "a", "b" };

            InvokeReplaceCollection(target, source);

            Assert.Equal(2, target.Count);
            Assert.Equal("a", target[0]);
            Assert.Equal("b", target[1]);
        }

        [Fact]
        public void ReplaceCollection_SourceMoreItems_NewItemsAdded()
        {
            var target = new ObservableCollection<string> { "a" };
            var source = new List<string> { "a", "b", "c" };

            InvokeReplaceCollection(target, source);

            Assert.Equal(3, target.Count);
            Assert.Equal("a", target[0]);
            Assert.Equal("b", target[1]);
            Assert.Equal("c", target[2]);
        }

        [Fact]
        public void ReplaceCollection_SourceDifferentItems_InPlaceReplaced()
        {
            var target = new ObservableCollection<string> { "x", "y", "z" };
            var source = new List<string> { "a", "b", "c" };

            InvokeReplaceCollection(target, source);

            Assert.Equal(3, target.Count);
            Assert.Equal("a", target[0]);
            Assert.Equal("b", target[1]);
            Assert.Equal("c", target[2]);
        }

        [Fact]
        public void ReplaceCollection_PartialOverlap_MixedReplaceAddRemove()
        {
            var target = new ObservableCollection<string> { "keep", "remove1", "remove2" };
            var source = new List<string> { "keep", "new1", "new2", "new3" };

            InvokeReplaceCollection(target, source);

            Assert.Equal(4, target.Count);
            Assert.Equal("keep", target[0]);
            Assert.Equal("new1", target[1]);
            Assert.Equal("new2", target[2]);
            Assert.Equal("new3", target[3]);
        }

        [Fact]
        public void ReplaceCollection_EmptySource_ClearsTarget()
        {
            var target = new ObservableCollection<string> { "a", "b", "c" };
            var source = new List<string>();

            InvokeReplaceCollection(target, source);

            Assert.Empty(target);
        }

        [Fact]
        public void ReplaceCollection_EmptyTarget_AddsAllSource()
        {
            var target = new ObservableCollection<string>();
            var source = new List<string> { "a", "b", "c" };

            InvokeReplaceCollection(target, source);

            Assert.Equal(3, target.Count);
            Assert.Equal("a", target[0]);
            Assert.Equal("b", target[1]);
            Assert.Equal("c", target[2]);
        }

        [Fact]
        public void ReplaceCollection_SourceIsNotIList_ForcesToListPath()
        {
            // Source as IEnumerable (not IList) forces the .ToList() path
            var target = new ObservableCollection<int> { 1, 2, 3 };
            IEnumerable<int> source = Enumerable.Range(10, 5); // IEnumerable<int>, not IList

            InvokeReplaceCollection(target, source);

            Assert.Equal(5, target.Count);
            Assert.Equal(10, target[0]);
            Assert.Equal(14, target[4]);
        }

        [Fact]
        public void ReplaceCollection_SameReference_RecognizesEqual()
        {
            // EqualityComparer<T>.Default uses reference equality for reference types
            // and value equality for value types
            var obj = new object();
            var target = new ObservableCollection<object> { obj };
            var source = new List<object> { obj };

            InvokeReplaceCollection(target, source);

            Assert.Single(target);
            Assert.Same(obj, target[0]);
        }

        [Fact]
        public void ReplaceCollection_ValueTypeEquality_DetectsEqual()
        {
            var target = new ObservableCollection<int> { 42, 7 };
            var source = new List<int> { 42, 7 };

            InvokeReplaceCollection(target, source);

            Assert.Equal(2, target.Count);
            Assert.Equal(42, target[0]);
            Assert.Equal(7, target[1]);
        }

        // ── GetActiveFindings tests (private instance method) ─────────────────

        private static MainWindow CreateUninitializedMainWindow()
{
            return (MainWindow)FormatterServices.GetUninitializedObject(
                typeof(MainWindow));
        }

        private static void SetField<T>(object instance, string fieldName, T value)
        {
            var field = typeof(MainWindow).GetField(
                fieldName,
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field!.SetValue(instance, value);
        }

        private static T GetField<T>(object instance, string fieldName)
        {
            var field = typeof(MainWindow).GetField(
                fieldName,
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            return (T)field!.GetValue(instance)!;
        }

        private static object InvokeInstanceMethod(object instance, string methodName)
        {
            var method = typeof(MainWindow).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            return method!.Invoke(instance, null)!;
        }

        [Fact]
        public void GetActiveFindings_ReturnsOnlyNonInfoSeverity()
        {
            var window = CreateUninitializedMainWindow();
            var findings = new List<AdHealthFinding>
            {
                new() { Severity = "Critical", Category = "DNS", Summary = "DNS failure" },
                new() { Severity = "Info", Category = "DNS", Summary = "DNS passed" },
                new() { Severity = "High", Category = "Replication", Summary = "Rep failure" },
                new() { Severity = "Medium", Category = "SYSVOL", Summary = "SYSVOL failure" },
                new() { Severity = "Low", Category = "Configuration", Summary = "Config issue" },
                new() { Severity = "Info", Category = "Infrastructure", Summary = "Passed" },
            };
            SetField(window, "allFindings", findings);

            var result = InvokeInstanceMethod(window, "GetActiveFindings");

            var activeFindings = Assert.IsAssignableFrom<IEnumerable<AdHealthFinding>>(result);
            var list = activeFindings.ToList();
            Assert.Equal(4, list.Count);
            Assert.All(list, f => Assert.NotEqual("Info", f.Severity));
            Assert.Contains(list, f => f.Severity == "Critical");
            Assert.Contains(list, f => f.Severity == "High");
            Assert.Contains(list, f => f.Severity == "Medium");
            Assert.Contains(list, f => f.Severity == "Low");
        }

        [Fact]
        public void GetActiveFindings_EmptyFindings_ReturnsEmpty()
        {
            var window = CreateUninitializedMainWindow();
            SetField(window, "allFindings", new List<AdHealthFinding>());

            var result = InvokeInstanceMethod(window, "GetActiveFindings");

            var activeFindings = Assert.IsAssignableFrom<IEnumerable<AdHealthFinding>>(result);
            Assert.Empty(activeFindings);
        }

        [Fact]
        public void GetActiveFindings_AllInfoSeverity_ReturnsEmpty()
        {
            var window = CreateUninitializedMainWindow();
            var findings = new List<AdHealthFinding>
            {
                new() { Severity = "Info", Category = "DNS", Summary = "Passed" },
                new() { Severity = "Info", Category = "Replication", Summary = "Passed" },
            };
            SetField(window, "allFindings", findings);

            var result = InvokeInstanceMethod(window, "GetActiveFindings");

            var activeFindings = Assert.IsAssignableFrom<IEnumerable<AdHealthFinding>>(result);
            Assert.Empty(activeFindings);
        }

        [Fact]
        public void GetActiveFindings_NullSeverity_TreatedAsInactive()
        {
            // IsActiveSeverity returns false for null (null severity is treated
            // as inactive, equivalent to "Info"). GetActiveFindings filters
            // eagerly, so null-severity findings are silently excluded.
            var window = CreateUninitializedMainWindow();
            var findings = new List<AdHealthFinding>
            {
                new() { Severity = null, Category = "Test", Summary = "No severity" },
            };
            SetField(window, "allFindings", findings);

            var result = InvokeInstanceMethod(window, "GetActiveFindings");
            var activeFindings = Assert.IsAssignableFrom<IEnumerable<AdHealthFinding>>(result);

            // Null severity → not active → excluded from result
            Assert.Empty(activeFindings);
        }

        // ── ApplyFindingsFilter tests (private instance method) ──────────────

        [Fact]
        public void ApplyFindingsFilter_FindingsPageNotBound_NoOp()
        {
            var window = CreateUninitializedMainWindow();
            SetField(window, "findingsPageBound", false);

            // Should not throw when findingsPageBound is false
            var ex = Record.Exception(() => InvokeInstanceMethod(window, "ApplyFindingsFilter"));
            Assert.Null(ex);
        }

        [Fact]
        public void ApplyFindingsFilter_FindingsPageBoundNoView_CreatesAndRefreshes()
        {
            var window = CreateUninitializedMainWindow();
            SetField(window, "findingsPageBound", true);
            // findingItemsView is null by default — EnsureFindingItemsView() will create it
            // EnsureFindingItemsView requires findingItems to be set
            var findingItems = new ObservableCollection<AdHealthFinding>();
            SetField(window, "findingItems", findingItems);

            // Should work: creates a view from findingItems, refreshes it
            var ex = Record.Exception(() => InvokeInstanceMethod(window, "ApplyFindingsFilter"));
            Assert.Null(ex);
        }

        // ── SyncFindingItems tests (private instance method) ─────────────────

        [Fact]
        public void SyncFindingItems_UpdatesFindingItemsFromAllFindings()
        {
            var window = CreateUninitializedMainWindow();
            var findings = new List<AdHealthFinding>
            {
                new() { Severity = "Critical", Category = "DNS", Summary = "Failure A" },
                new() { Severity = "High", Category = "Replication", Summary = "Failure B" },
            };
            SetField(window, "allFindings", findings);
            var findingItems = new ObservableCollection<AdHealthFinding>
            {
                new() { Severity = "Low", Category = "Old", Summary = "Old finding" },
            };
            SetField(window, "findingItems", findingItems);
            SetField(window, "findingsPageBound", false);

            InvokeInstanceMethod(window, "SyncFindingItems");

            Assert.Equal(2, findingItems.Count);
            Assert.Equal("Failure A", findingItems[0].Summary);
            Assert.Equal("Failure B", findingItems[1].Summary);
        }

        [Fact]
        public void SyncFindingItems_EmptyAllFindings_ClearsFindingItems()
        {
            var window = CreateUninitializedMainWindow();
            SetField(window, "allFindings", new List<AdHealthFinding>());
            var findingItems = new ObservableCollection<AdHealthFinding>
            {
                new() { Severity = "Low", Category = "Old", Summary = "Old finding" },
            };
            SetField(window, "findingItems", findingItems);
            SetField(window, "findingsPageBound", false);

            InvokeInstanceMethod(window, "SyncFindingItems");

            Assert.Empty(findingItems);
        }

        [Fact]
        public void SyncFindingItems_PageBound_UpdatesCollectionBeforeViewRefresh()
        {
            // When page is bound, SyncFindingItems calls ReplaceCollection first, then
            // EnsureFindingItemsView().DeferRefresh() which requires STA thread.
            // We verify that ReplaceCollection completed before the STA exception.
            var window = CreateUninitializedMainWindow();
            var findings = new List<AdHealthFinding>
            {
                new() { Severity = "Critical", Category = "DNS", Summary = "Failure" },
            };
            SetField(window, "allFindings", findings);
            var findingItems = new ObservableCollection<AdHealthFinding>();
            SetField(window, "findingItems", findingItems);
            SetField(window, "findingsPageBound", true);

            // EnsureFindingItemsView requires STA; we catch the exception
            // to verify ReplaceCollection completed first
            try
            {
                InvokeInstanceMethod(window, "SyncFindingItems");
            }
            catch (TargetInvocationException) { /* STA or WPF null ref after ReplaceCollection */ }

            // ReplaceCollection should have completed before the view refresh
            Assert.Single(findingItems);
            Assert.Equal("Failure", findingItems[0].Summary);
        }

        // ── SyncResultItems tests (private instance method) ──────────────────

        [Fact]
        public void SyncResultItems_UpdatesResultItemsFromAllResults()
        {
            var window = CreateUninitializedMainWindow();
            var results = new List<TestResult>
            {
                new() { Service = "DNS", Server = "DC01", Result = "PASS" },
                new() { Service = "Replication", Server = "DC02", Result = "FAIL" },
            };
            SetField(window, "allResults", results);
            var resultItems = new ObservableCollection<TestResult>
            {
                new() { Service = "Old", Server = "OldDC", Result = "PASS" },
            };
            SetField(window, "resultItems", resultItems);
            var logResultItems = new ObservableCollection<TestResult>();
            SetField(window, "logResultItems", logResultItems);

            // SyncResultItems calls ReplaceCollection on both, then
            // RefreshLogsWorkspace() and UpdateActionButtonStates() which may
            // throw on uninitialized WPF controls. We catch that to verify
            // the ReplaceCollection part completed.
            try
            {
                InvokeInstanceMethod(window, "SyncResultItems");
            }
            catch (TargetInvocationException) { /* WPF null ref after ReplaceCollection */ }

            Assert.Equal(2, resultItems.Count);
            Assert.Equal("DNS", resultItems[0].Service);
            Assert.Equal("DC01", resultItems[0].Server);
            Assert.Equal("PASS", resultItems[0].Result);

            Assert.Equal(2, logResultItems.Count);
            Assert.Equal("DNS", logResultItems[0].Service);
        }

        [Fact]
        public void SyncResultItems_EmptyAllResults_ClearsBothCollections()
        {
            var window = CreateUninitializedMainWindow();
            SetField(window, "allResults", new List<TestResult>());
            var resultItems = new ObservableCollection<TestResult>
            {
                new() { Service = "Old", Server = "OldDC", Result = "PASS" },
            };
            SetField(window, "resultItems", resultItems);
            var logResultItems = new ObservableCollection<TestResult>
            {
                new() { Service = "Old2", Server = "OldDC2", Result = "FAIL" },
            };
            SetField(window, "logResultItems", logResultItems);

            try
            {
                InvokeInstanceMethod(window, "SyncResultItems");
            }
            catch (TargetInvocationException) { /* WPF null ref after ReplaceCollection */ }

            Assert.Empty(resultItems);
            Assert.Empty(logResultItems);
        }

        // ── ForceRefreshDashboard tests (private instance method) ────────────

        [Fact]
        public void ForceRefreshDashboard_NullsDashboardHash()
        {
            var window = CreateUninitializedMainWindow();
            SetField(window, "_dashboardHash", "some-hash-value");

            // RefreshDashboardNow will hit WPF null refs on uninitialized window,
            // but _dashboardHash should be nulled before that.
            try
            {
                InvokeInstanceMethod(window, "ForceRefreshDashboard");
            }
            catch (TargetInvocationException) { /* WPF null ref after hash cleared */ }

            var hash = GetField<string?>(window, "_dashboardHash");
            Assert.Null(hash);
        }

        [Fact]
        public void ForceRefreshDashboard_AlreadyNullHash_StaysNull()
        {
            var window = CreateUninitializedMainWindow();
            // _dashboardHash is already null by default on uninitialized object

            try
            {
                InvokeInstanceMethod(window, "ForceRefreshDashboard");
            }
            catch (TargetInvocationException) { /* WPF null ref */ }

            var hash = GetField<string?>(window, "_dashboardHash");
            Assert.Null(hash);
        }
    }
}
