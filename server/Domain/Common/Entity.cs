namespace EndpointPlatform.Domain.Common;

/// <summary>
/// Base class for every persisted entity.
/// </summary>
/// <remarks>
/// Identifiers are UUIDv7: globally unique like a UUIDv4, but time-ordered, so
/// primary-key inserts stay sequential and B-tree index pages do not fragment
/// the way random v4 keys cause them to. Generating them in the domain (rather
/// than letting PostgreSQL do it) keeps the entity valid the moment it is
/// constructed, before any round trip to the database.
/// </remarks>
public abstract class Entity : IEquatable<Entity>
{
    protected Entity()
        : this(Guid.CreateVersion7())
    {
    }

    protected Entity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Entity identifier must not be the empty GUID.", nameof(id));
        }

        Id = id;
    }

    public Guid Id { get; private set; }

    public bool Equals(Entity? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        // Entities of different types are never equal even if the ids collide.
        return GetType() == other.GetType() && Id == other.Id;
    }

    public override bool Equals(object? obj) => Equals(obj as Entity);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity? left, Entity? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Entity? left, Entity? right) => !(left == right);
}
