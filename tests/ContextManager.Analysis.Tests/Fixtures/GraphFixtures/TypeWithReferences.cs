namespace GraphFixtures;

public interface IRepository<T> { }

public class Attraction { }

public class Location { }

public class Tour { }

public class Booking { }

// Target type: references user-defined types in parameter, return, property, field,
// generic arg, and a local variable in a method body (local must NOT produce a REFERENCES edge).
public class TourService
{
    // Property of user-defined type → REFERENCES TourService→Tour
    public Tour CurrentTour { get; set; } = null!;

    // Field of user-defined type → REFERENCES TourService→Booking
    private Booking _lastBooking = null!;

    // Parameter type → REFERENCES TourService→Location
    // Return type → REFERENCES TourService→Attraction
    public Attraction GetAttractionNear(Location loc)
    {
        return new Attraction();
    }

    // Generic argument → REFERENCES TourService→IRepository and TourService→Attraction
    public IRepository<Attraction> GetAttractionRepository()
    {
        return null!;
    }

    // Local variable of user-defined type — must NOT produce a REFERENCES edge
    public void ProcessLocally()
    {
        var localTour = new Tour();
        _ = localTour;
    }
}
