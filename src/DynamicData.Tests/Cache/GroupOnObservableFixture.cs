﻿using System;
using System.Linq;
using Bogus;
using DynamicData.Tests.Domain;
using DynamicData.Binding;
using System.Reactive.Linq;
using FluentAssertions;
using Xunit;

using Person = DynamicData.Tests.Domain.Person;
using System.Threading.Tasks;
using DynamicData.Kernel;

namespace DynamicData.Tests.Cache;

public class GroupOnObservableFixture : IDisposable
{
#if DEBUG
    private const int InitialCount = 7;
    private const int AddCount = 5;
    private const int RemoveCount = 3;
    private const int UpdateCount = 2;
#else
    private const int InitialCount = 103;
    private const int AddCount = 53;
    private const int RemoveCount = 37;
    private const int UpdateCount = 101;
#endif
    private readonly SourceCache<Person, string> _cache = new (p => p.UniqueKey);
    private readonly ChangeSetAggregator<Person, string> _results;
    private readonly GroupChangeSetAggregator<Person, string, Color> _groupResults;
    private readonly Faker<Person> _faker;
    private readonly Randomizer _randomizer = new(0x3141_5926);

    public GroupOnObservableFixture()
    {
        _faker = Fakers.Person.Clone().WithSeed(_randomizer);
        _results = _cache.Connect().AsAggregator();
        _groupResults = _cache.Connect().GroupOnObservable(CreateFavoriteColorObservable).AsAggregator();
    }

    [Fact]
    public void ResultContainsAllInitialChildren()
    {
        // Arrange

        // Act
        _cache.AddOrUpdate(_faker.Generate(InitialCount));

        // Assert
        _results.Data.Count.Should().Be(InitialCount);
        _results.Messages.Count.Should().Be(1, "The child observables fire on subscription so everything should appear as a single changeset");
        VerifyGroupingResults();
    }

    [Fact]
    public void ResultContainsAddedValues()
    {
        // Arrange
        _cache.AddOrUpdate(_faker.Generate(InitialCount));

        // Act
        _cache.AddOrUpdate(_faker.Generate(AddCount));

        // Assert
        _results.Data.Count.Should().Be(InitialCount + AddCount);
        _results.Messages.Count.Should().Be(2, "Initial Adds and then the subsequent Additions should each be a single message");
        VerifyGroupingResults();
    }

    [Fact]
    public void ResultDoesNotContainRemovedValues()
    {
        // Arrange
        _cache.AddOrUpdate(_faker.Generate(InitialCount));

        // Act
        _cache.RemoveKeys(_randomizer.ListItems(_cache.Items.ToList(), RemoveCount).Select(p => p.UniqueKey));

        // Assert
        _results.Data.Count.Should().Be(InitialCount - RemoveCount);
        _results.Messages.Count.Should().Be(2, "1 for Adds and 1 for Removes");
        VerifyGroupingResults();
    }

    [Fact]
    public void ResultContainsUpdatedValues()
    {
        // Arrange
        _cache.AddOrUpdate(_faker.Generate(InitialCount));
        var replacements = _randomizer.ListItems(_cache.Items.ToList(), UpdateCount)
            .Select(replacePerson => Person.CloneUniqueId(_faker.Generate(), replacePerson));

        // Act
        _cache.AddOrUpdate(replacements);

        // Assert
        _results.Data.Count.Should().Be(InitialCount, "Only replacements were made");
        _results.Messages.Count.Should().Be(2, "1 for Adds and 1 for Updates");
        VerifyGroupingResults();
    }

    [Fact]
    public void GroupRemovedWhenEmpty()
    {
        // Arrange
        _cache.AddOrUpdate(_faker.Generate(InitialCount));
        var usedColorList = _cache.Items.Select(p => p.FavoriteColor).Distinct().ToList();
        var removeColor = _randomizer.ListItem(usedColorList);
        var colorCount = usedColorList.Count;

        // Act
        _cache.Edit(updater => updater.Remove(updater.Items.Where(p => p.FavoriteColor == removeColor).Select(p => p.UniqueKey)));

        // Assert
        _cache.Items.Select(p => p.FavoriteColor).Distinct().Count().Should().Be(colorCount - 1);
        _results.Messages.Count.Should().Be(2, "1 for Adds and 1 for Removes");
        _groupResults.Messages.Count.Should().Be(2, "1 for Adds and 1 for Removes");
        _groupResults.Summary.Overall.Adds.Should().Be(colorCount);
        _groupResults.Summary.Overall.Removes.Should().Be(1);
        VerifyGroupingResults();
    }

    [Fact]
    public void GroupNotRemovedIfAddedBackImmediately()
    {
        // Arrange
        _cache.AddOrUpdate(_faker.Generate(InitialCount));
        var usedColorList = _cache.Items.Select(p => p.FavoriteColor).Distinct().ToList();
        var removeColor = _randomizer.ListItem(usedColorList);
        var colorCount = usedColorList.Count;

        // Act
        _cache.Edit(updater =>
        {
            updater.Remove(updater.Items.Where(p => p.FavoriteColor == removeColor).Select(p => p.UniqueKey));
            var newPerson = _faker.Generate();
            newPerson.FavoriteColor = removeColor;
            updater.AddOrUpdate(newPerson);
        });

        // Assert
        _cache.Items.Select(p => p.FavoriteColor).Distinct().Count().Should().Be(colorCount);
        _results.Messages.Count.Should().Be(2, "1 for Adds and 1 for Other Added Value");
        _groupResults.Messages.Count.Should().Be(1, "Shouldn't be removed/re-added");
        _groupResults.Summary.Overall.Adds.Should().Be(colorCount);
        _groupResults.Summary.Overall.Removes.Should().Be(0);
        VerifyGroupingResults();
    }

    [Fact]
    public void GroupingSequenceCompletesWhenEmpty()
    {
        // Arrange
        _cache.AddOrUpdate(_faker.Generate(InitialCount));
        var usedColorList = _cache.Items.Select(p => p.FavoriteColor).Distinct().ToList();
        var removeColor = _randomizer.ListItem(usedColorList);

        var results = _cache.Connect().GroupOnObservable(CreateFavoriteColorObservable)
            .Filter(grp => grp.Key == removeColor)
            .Take(1)
            .MergeMany(grp => grp.Cache.Connect())
            .AsAggregator();

        // Act
        _cache.Edit(updater => updater.Remove(updater.Items.Where(p => p.FavoriteColor == removeColor).Select(p => p.UniqueKey)));

        // Assert
        results.IsCompleted.Should().BeTrue();
        VerifyGroupingResults();
    }

    [Fact]
    public void AllSequencesCompleteWhenSourceIsDisposed()
    {
        // Arrange
        _cache.AddOrUpdate(_faker.Generate(InitialCount));

        var results = _cache.Connect().GroupOnObservable(CreateFavoriteColorObservable)
            .MergeMany(grp => grp.Cache.Connect())
            .AsAggregator();

        // Act
        _cache.Dispose();

        // Assert
        results.IsCompleted.Should().BeTrue();
        VerifyGroupingResults();
    }

    [Fact]
    public void AllGroupsRemovedWhenCleared()
    {
        // Arrange
        _cache.AddOrUpdate(_faker.Generate(InitialCount));
        var usedColorList = _cache.Items.Select(p => p.FavoriteColor).Distinct().ToList();
        var colorCount = usedColorList.Count;

        // Act
        _cache.Clear();

        // Assert
        _cache.Items.Count().Should().Be(0);
        _results.Messages.Count.Should().Be(2, "1 for Adds and 1 for Removes");
        _groupResults.Summary.Overall.Adds.Should().Be(colorCount);
        _groupResults.Summary.Overall.Removes.Should().Be(colorCount);
        VerifyGroupingResults();
    }

    [Fact]
    public void ResultsContainsCorrectRegroupedValues()
    {
        // Arrange
        _cache.AddOrUpdate(_faker.Generate(InitialCount));

        // Act
        Enumerable.Range(0, UpdateCount).ForEach(_ => RandomFavoriteColorChange());

        // Assert
        VerifyGroupingResults();
    }

    [Fact]
    public async Task ResultsContainsCorrectRegroupedValuesAsync()
    {
        // Arrange
        _cache.AddOrUpdate(_faker.Generate(InitialCount));
        var tasks = Enumerable.Range(0, UpdateCount).Select(_ => Task.Run(RandomFavoriteColorChange));

        // Act
        await Task.WhenAll(tasks.ToArray());

        // Assert
        VerifyGroupingResults();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResultCompletesOnlyWhenSourceCompletes(bool completeSource)
    {
        // Arrange
        _cache.AddOrUpdate(_faker.Generate(InitialCount));

        // Act
        if (completeSource)
        {
            _cache.Dispose();
        }

        // Assert
        _results.IsCompleted.Should().Be(completeSource);
        _groupResults.IsCompleted.Should().Be(completeSource);
    }

    [Fact]
    public void ResultFailsIfSourceFails()
    {
        // Arrange
        _cache.AddOrUpdate(_faker.Generate(InitialCount));
        var expectedError = new Exception("Expected");
        var throwObservable = Observable.Throw<IChangeSet<Person, string>>(expectedError);
        using var results = _cache.Connect().Concat(throwObservable).GroupOnObservable(CreateFavoriteColorObservable).AsAggregator();

        // Act
        _cache.Dispose();

        // Assert
        results.Error.Should().Be(expectedError);
    }

    [Fact]
    public void ResultFailsIfGroupObservableFails()
    {
        // Arrange
        _cache.AddOrUpdate(_faker.Generate(InitialCount));
        var expectedError = new Exception("Expected");
        var throwObservable = Observable.Throw<Color>(expectedError);

        // Act
        using var results = _cache.Connect().GroupOnObservable((person, key) => CreateFavoriteColorObservable(person, key).Take(1).Concat(throwObservable)).AsAggregator();

        // Assert
        results.Error.Should().Be(expectedError);
    }

    [Fact]
    public void OnErrorFiresIfSelectorThrows()
    {
        // Arrange
        _cache.AddOrUpdate(_faker.Generate(InitialCount));
        var expectedError = new Exception("Expected");

        // Act
        using var results = _cache.Connect().GroupOnObservable<Person, string, Color>(_ => throw expectedError).AsAggregator();

        // Assert
        results.Error.Should().Be(expectedError);
    }

    public void Dispose()
    {
        _groupResults.Dispose();
        _results.Dispose();
        _cache.Dispose();
    }

    private void RandomFavoriteColorChange()
    {
        var person = _randomizer.ListItem(_cache.Items.ToList());
        lock (person)
        {
            // Pick a new favorite color
            person.FavoriteColor = _randomizer.RandomColor(person.FavoriteColor);
        }
    }

    private void VerifyGroupingResults() =>
        VerifyGroupingResults(_cache, _results, _groupResults);

    private static void VerifyGroupingResults(ISourceCache<Person, string> cache, ChangeSetAggregator<Person, string> cacheResults, GroupChangeSetAggregator<Person, string, Color> groupResults)
    {
        var expectedPersons = cache.Items.ToList();
        var expectedGroupings = cache.Items.GroupBy(p => p.FavoriteColor).ToList();

        // These datasets should be equivalent
        expectedPersons.Should().BeEquivalentTo(cacheResults.Data.Items);
        groupResults.Groups.Keys.Should().BeEquivalentTo(expectedGroupings.Select(g => g.Key));

        // Check each group
        expectedGroupings.ForEach(grouping => grouping.Should().BeEquivalentTo(groupResults.Groups.Lookup(grouping.Key).Value.Data.Items));
    }

    private static IObservable<Color> CreateFavoriteColorObservable(Person person, string key) =>
         person.WhenPropertyChanged(p => p.FavoriteColor).Select(change => change.Value);
}
