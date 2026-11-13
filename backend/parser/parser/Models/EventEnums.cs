namespace Bashkiria.Events.Parser.Models;

public enum PublicationStatus { Draft, Published, Cancelled }
public enum SourceEventStatus { Unknown, Scheduled, Cancelled, Postponed, Rescheduled, SoldOut }
public enum EventCategory { Other, Concert, Theatre, Exhibition, Festival, Cinema, Excursion, Workshop, Lecture }
public enum CollectionRunStatus { Queued, Running, Completed, CompletedWithErrors, Failed, Cancelled }
