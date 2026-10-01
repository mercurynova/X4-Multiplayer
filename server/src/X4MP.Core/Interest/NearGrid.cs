using X4MP.Core.World;

namespace X4MP.Core.Interest;

/// <summary>
/// The Near-tier index (server-design 2.5): a uniform 3-D grid with a cell size of <c>NearRadius</c> per sector, maintained
/// incrementally as entities move. A radius query touches at most 27 cells. Grids exist only for sectors that currently have a
/// player in them (<see cref="Activate"/> builds one from the mirror, <see cref="Deactivate"/> drops it), so the cost is bounded by
/// the number of players, not by the galaxy. Positions are the wire's 1/64 m units. Actor-thread only.
/// </summary>
public sealed class NearGrid
{
    /// <summary>Wire position units per metre (<c>px = metres * 64</c>).</summary>
    public const int UnitsPerMetre = 64;

    private readonly Dictionary<ulong, List<MirrorEntity>> _cells = [];
    private readonly Dictionary<ushort, int> _cellsPerSector = [];
    private readonly Stack<List<MirrorEntity>> _pool = new();
    private readonly HashSet<ushort> _active = [];
    private long _cellUnits;

    public NearGrid(int cellMetres) => Configure(cellMetres);

    public int CellMetres => (int)(_cellUnits / UnitsPerMetre);

    public int ActiveSectorCount => _active.Count;

    public int CellCount => _cells.Count;

    public bool IsActive(ushort sector) => _active.Contains(sector);

    /// <summary>Changes the cell size; the caller re-activates the grids afterwards (<see cref="Clear"/> first).</summary>
    public void Configure(int cellMetres)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cellMetres, 1);
        _cellUnits = (long)cellMetres * UnitsPerMetre;
    }

    public void Clear()
    {
        foreach (var list in _cells.Values)
        {
            foreach (var e in list)
            {
                e.GridSlot = -1;
            }

            list.Clear();
            _pool.Push(list);
        }

        _cells.Clear();
        _cellsPerSector.Clear();
        _active.Clear();
    }

    /// <summary>Builds the grid of a sector from the mirror's transient entities.</summary>
    public void Activate(ushort sector, ReadOnlySpan<MirrorEntity> entities)
    {
        if (!_active.Add(sector))
        {
            return;
        }

        foreach (var e in entities)
        {
            Add(e);
        }
    }

    public void Deactivate(ushort sector)
    {
        if (!_active.Remove(sector))
        {
            return;
        }

        var doomed = new List<ulong>();
        foreach (var (key, list) in _cells)
        {
            if ((ushort)(key >> 48) == sector)
            {
                doomed.Add(key);
                foreach (var e in list)
                {
                    e.GridSlot = -1;
                }

                list.Clear();
                _pool.Push(list);
            }
        }

        foreach (var key in doomed)
        {
            _cells.Remove(key);
        }

        _cellsPerSector.Remove(sector);
    }

    /// <summary>Adds an entity if its sector has a grid.</summary>
    public void Add(MirrorEntity entity)
    {
        if (entity.GridSlot >= 0 || !_active.Contains(entity.Sector))
        {
            return;
        }

        ulong key = KeyOf(entity.Sector, entity.Px, entity.Py, entity.Pz);
        Insert(entity, key);
    }

    public void Remove(MirrorEntity entity)
    {
        if (entity.GridSlot < 0)
        {
            return;
        }

        Detach(entity);
    }

    /// <summary>Re-files an entity after its position or sector changed.</summary>
    public void Move(MirrorEntity entity)
    {
        if (!_active.Contains(entity.Sector))
        {
            Remove(entity);
            return;
        }

        ulong key = KeyOf(entity.Sector, entity.Px, entity.Py, entity.Pz);
        if (entity.GridSlot >= 0)
        {
            if (entity.GridKey == key)
            {
                return;
            }

            Detach(entity);
        }

        Insert(entity, key);
    }

    /// <summary>
    /// Adds every entity of <paramref name="sector"/> within <paramref name="radiusMetres"/> of the point (wire units) to
    /// <paramref name="results"/>. Returns false when the sector has no grid.
    /// </summary>
    public bool Query(ushort sector, int px, int py, int pz, int radiusMetres, List<MirrorEntity> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (!_active.Contains(sector))
        {
            return false;
        }

        long radius = (long)radiusMetres * UnitsPerMetre;
        long radiusSq = radius * radius;
        long cx0 = Cell(px - radius);
        long cx1 = Cell(px + radius);
        long cy0 = Cell(py - radius);
        long cy1 = Cell(py + radius);
        long cz0 = Cell(pz - radius);
        long cz1 = Cell(pz + radius);
        for (long cx = cx0; cx <= cx1; cx++)
        {
            for (long cy = cy0; cy <= cy1; cy++)
            {
                for (long cz = cz0; cz <= cz1; cz++)
                {
                    if (!_cells.TryGetValue(Pack(sector, cx, cy, cz), out var list))
                    {
                        continue;
                    }

                    foreach (var e in list)
                    {
                        long dx = e.Px - (long)px;
                        long dy = e.Py - (long)py;
                        long dz = e.Pz - (long)pz;
                        if ((dx * dx) + (dy * dy) + (dz * dz) <= radiusSq)
                        {
                            results.Add(e);
                        }
                    }
                }
            }
        }

        return true;
    }

    /// <summary>Squared distance in wire units.</summary>
    public static long DistanceSquared(MirrorEntity e, int px, int py, int pz)
    {
        long dx = e.Px - (long)px;
        long dy = e.Py - (long)py;
        long dz = e.Pz - (long)pz;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private void Insert(MirrorEntity entity, ulong key)
    {
        if (!_cells.TryGetValue(key, out var list))
        {
            list = _pool.TryPop(out var pooled) ? pooled : [];
            _cells[key] = list;
            ushort sector = entity.Sector;
            _cellsPerSector[sector] = _cellsPerSector.GetValueOrDefault(sector) + 1;
        }

        entity.GridKey = key;
        entity.GridSlot = list.Count;
        list.Add(entity);
    }

    private void Detach(MirrorEntity entity)
    {
        if (!_cells.TryGetValue(entity.GridKey, out var list))
        {
            entity.GridSlot = -1;
            return;
        }

        int slot = entity.GridSlot;
        int last = list.Count - 1;
        if (slot <= last && ReferenceEquals(list[slot], entity))
        {
            var moved = list[last];
            list[slot] = moved;
            moved.GridSlot = slot;
            list.RemoveAt(last);
        }

        entity.GridSlot = -1;
        if (list.Count == 0)
        {
            _cells.Remove(entity.GridKey);
            _pool.Push(list);
            ushort sector = (ushort)(entity.GridKey >> 48);
            if (_cellsPerSector.TryGetValue(sector, out int n))
            {
                if (n <= 1)
                {
                    _cellsPerSector.Remove(sector);
                }
                else
                {
                    _cellsPerSector[sector] = n - 1;
                }
            }
        }
    }

    private long Cell(long position) => Math.DivRem(position, _cellUnits, out long rem) - (rem < 0 ? 1 : 0);

    private ulong KeyOf(ushort sector, int px, int py, int pz) => Pack(sector, Cell(px), Cell(py), Cell(pz));

    private static ulong Pack(ushort sector, long cx, long cy, long cz) =>
        ((ulong)sector << 48) | (Clamp16(cx) << 32) | (Clamp16(cy) << 16) | Clamp16(cz);

    private static ulong Clamp16(long cell) => (ulong)(Math.Clamp(cell, -32768, 32767) + 32768);
}
