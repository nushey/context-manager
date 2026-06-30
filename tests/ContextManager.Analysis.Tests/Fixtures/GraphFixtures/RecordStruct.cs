namespace GraphFixtures;

// record struct has TypeKind.Struct AND IsRecord — exercises the shared classifier's
// IsRecord-before-Struct ordering. The Sum member yields a CONTAINS edge whose source
// node is Point, so the extractor's classification of the record struct is observable.
public record struct Point(int X, int Y)
{
    public int Sum() => X + Y;
}
