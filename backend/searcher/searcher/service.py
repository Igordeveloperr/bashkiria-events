from uuid import UUID

from sqlalchemy import func, select
from sqlalchemy.orm import Session, selectinload

from searcher.models import Event
from searcher.queries import (
    filter_conditions,
    normalize,
    normalized,
    period_conditions,
    price_bounds,
    similarity,
    text_rank,
)
from searcher.schemas import (
    EventPage,
    EventResponse,
    Pagination,
    SchedulePage,
    ScheduleQuery,
    SearchQuery,
    SimilarEventResponse,
    SimilarPage,
    SimilarQuery,
)


class EventNotFoundError(LookupError):
    pass


class EventSearchService:
    def __init__(self, session: Session):
        self.session = session

    def get_event(self, event_id: UUID) -> EventResponse:
        event = self.session.scalar(
            select(Event)
            .where(Event.id == event_id)
            .options(selectinload(Event.sources))
        )
        if event is None:
            raise EventNotFoundError(str(event_id))
        return EventResponse.model_validate(event)

    def search_events(self, query: SearchQuery) -> EventPage:
        statement = select(Event).where(*filter_conditions(query))
        date_order = (
            Event.start_date.asc().nulls_last(),
            Event.start_time.asc().nulls_last(),
        )
        low_price, high_price = price_bounds()
        sorts = {
            "date_asc": date_order,
            "date_desc": (
                Event.start_date.desc().nulls_last(),
                Event.start_time.desc().nulls_last(),
            ),
            "price_asc": (
                low_price.asc().nulls_last(),
                *date_order,
            ),
            "price_desc": (
                high_price.desc().nulls_last(),
                *date_order,
            ),
            "updated_desc": (Event.updated_at.desc(),),
        }
        sort = query.sort or ("relevance" if query.q else "date_asc")
        order = (
            (text_rank(query.q).desc(), *date_order)
            if sort == "relevance"
            else sorts[sort]
        )
        return self._page(statement.order_by(*order, Event.id), query)

    def get_venue_schedule(self, query: ScheduleQuery) -> SchedulePage:
        conditions = [
            Event.status == "Published",
            normalized(Event.venue) == normalize(query.venue),
            *period_conditions(query.date_from, query.date_to),
        ]
        if query.city:
            conditions.append(normalized(Event.city) == normalize(query.city))
        if query.address:
            conditions.append(
                normalized(Event.address).contains(
                    normalize(query.address), autoescape=True
                )
            )
        statement = (
            select(Event)
            .where(*conditions)
            .order_by(
                Event.start_date.asc().nulls_last(),
                Event.start_time.asc().nulls_last(),
                Event.id,
            )
        )
        page = self._page(statement, query)
        return SchedulePage(
            **page.model_dump(),
            venue=query.venue,
            date_from=query.date_from,
            date_to=query.date_to,
        )

    def find_similar(self, event_id: UUID, query: SimilarQuery) -> SimilarPage:
        reference = self.session.get(Event, event_id)
        if reference is None:
            raise EventNotFoundError(str(event_id))
        score, candidate = similarity(reference)
        statement = (
            select(Event, score.label("score"))
            .where(Event.id != event_id, candidate, score >= query.min_score)
            .order_by(
                score.desc(),
                Event.start_date.asc().nulls_last(),
                Event.start_time.asc().nulls_last(),
                Event.id,
            )
        )
        total, rows = self._rows(statement, query)
        items = [
            SimilarEventResponse(
                **EventResponse.model_validate(event).model_dump(), score=float(score)
            )
            for event, score in rows
        ]
        return SimilarPage(
            items=items, reference_event_id=event_id, **self._metadata(total, query)
        )

    def _rows(self, statement, query: Pagination):
        total = self.session.scalar(
            statement.with_only_columns(
                func.count(), maintain_column_froms=True
            ).order_by(None)
        )
        # Load sources once for this page, after LIMIT; no duplicate events or N+1 queries.
        rows = self.session.execute(
            statement.options(selectinload(Event.sources))
            .limit(query.page_size)
            .offset((query.page - 1) * query.page_size)
        ).all()
        return total, rows

    def _page(self, statement, query: Pagination) -> EventPage:
        total, rows = self._rows(statement, query)
        return EventPage(
            items=[EventResponse.model_validate(row[0]) for row in rows],
            **self._metadata(total, query),
        )

    @staticmethod
    def _metadata(total: int, query: Pagination):
        pages = (total + query.page_size - 1) // query.page_size
        return dict(
            total=total,
            page=query.page,
            page_size=query.page_size,
            pages=pages,
            has_next=query.page < pages,
            has_previous=query.page > 1 and total > 0,
        )
