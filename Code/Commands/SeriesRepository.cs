using System;

namespace TinyGenerator.Services.Commands;

internal sealed class SeriesRepository
{
    private readonly DatabaseService _database;

    public SeriesRepository(DatabaseService database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public long Save(SeriesBuildResult result)
    {
        if (result == null) throw new ArgumentNullException(nameof(result));

        var serieId = _database.InsertSeries(result.Series);
        _database.InsertSeriesCharacters(serieId, result.Characters);
        _database.InsertSeriesEpisodes(serieId, result.Episodes);
        return serieId;
    }
}
