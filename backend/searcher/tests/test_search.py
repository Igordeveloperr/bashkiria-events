from datetime import UTC, datetime, timedelta
from uuid import uuid4

import pytest
from conftest import START
from sqlalchemy import inspect, text

from searcher.main import DatabaseSession
from searcher.models import Event, EventSource


def ids(response):
    assert response.status_code == 200, response.text
    return [item["id"] for item in response.json()["items"]]


def test_returns_all_migrated_event_and_source_columns(client, event_factory, database):
    event_id = event_factory(source_count=2)
    response = client.get(f"/events/{event_id}")
    assert response.status_code == 200, response.text
    payload = response.json()
    inspector = inspect(database[0])
    event_columns = {
        column["name"] for column in inspector.get_columns("parser_events")
    }
    source_columns = {
        column["name"] for column in inspector.get_columns("parser_event_sources")
    }
    assert event_columns == set(Event.__table__.columns.keys())
    assert source_columns == set(EventSource.__table__.columns.keys())
    assert event_columns <= payload.keys()
    assert len(payload["sources"]) == 2
    assert source_columns <= payload["sources"][0].keys()
    assert payload["sources"][0]["candidate_json"]["extra"]["preserved"] == [1, 2, 3]
    assert payload["description"] == "Музыканты исполняют джазовые композиции."
    assert payload["price_min"] == "500.00"
    assert payload["time_zone"] == "Asia/Yekaterinburg"
    assert ids(client.get("/events")) == [event_id]


def test_pagination_is_stable_counts_events_once_and_handles_empty_pages(
    client, event_factory
):
    expected = sorted(event_factory(source_count=2) for _ in range(5))
    pages = [
        client.get("/events", params={"page": page, "page_size": 2}).json()
        for page in (1, 2, 3, 4)
    ]
    assert [item["id"] for page in pages for item in page["items"]] == expected
    assert all(page["total"] == 5 and page["pages"] == 3 for page in pages)
    assert pages[0]["has_previous"] is False
    assert pages[0]["has_next"] is True
    assert pages[2]["has_next"] is False
    assert pages[3]["items"] == []
    assert pages[3]["total"] == 5


def test_default_status_and_explicit_statuses(client, event_factory):
    published = event_factory()
    draft = event_factory(status="Draft")
    cancelled = event_factory(status="Cancelled")
    assert ids(client.get("/events")) == [published]
    assert set(
        ids(
            client.get(
                "/events", params=[("statuses", "Draft"), ("statuses", "Cancelled")]
            )
        )
    ) == {draft, cancelled}


def test_text_stemming_substrings_typos_and_yo(client, event_factory):
    jazz = event_factory(title="Джазовый вечер в Уфе")
    tree = event_factory(
        title="Ёлка у Деда Мороза", description="Новогоднее представление"
    )
    assert ids(client.get("/events", params={"q": "музыкант"})) == [jazz]
    assert ids(client.get("/events", params={"q": "ДЖАЗОВЫЙ ВЕЧЕР"})) == [jazz]
    assert ids(client.get("/events", params={"q": "джазовй вечер в уфе"})) == [jazz]
    assert ids(client.get("/events", params={"q": "елка"})) == [tree]
    assert ids(client.get("/events", params={"q": "ёлка", "sort": "relevance"})) == [
        tree
    ]


def test_filters_combine_and_sources_do_not_duplicate(client, event_factory):
    matching = event_factory(source_count=2)
    event_factory(city="Стерлитамак")
    event_factory(category="Theatre")
    event_factory(source_id="other")
    event_factory(source_status="SoldOut")
    params = dict(
        city=" уФа ",
        venue="башкортостан",
        address="Ленина",
        language="рус",
        categories="Concert",
        source_id="GKZ",
        source_statuses="Scheduled",
        confidence_min="0.9",
        age_limit_max=12,
        duration_from=90,
        duration_to=150,
        start_date_from="2035-01-10",
        start_date_to="2035-01-10",
        updated_since="2035-01-01T00:00:00Z",
        verified_since="2035-01-01T00:00:00Z",
    )
    response = client.get("/events", params=params)
    assert ids(response) == [matching]
    assert response.json()["total"] == 1


def test_tags_any_all_case_and_repeated_values(client, event_factory):
    both = event_factory(tags=["Джаз", "музыка", "джаз"])
    one = event_factory(tags=["Музыка"])
    event_factory(tags=[])
    tags = [("tags", "ДЖАЗ"), ("tags", "музыка"), ("tags", "джаз")]
    assert ids(client.get("/events", params=tags)) == [both]
    assert set(ids(client.get("/events", params=tags + [("tags_mode", "any")]))) == {
        both,
        one,
    }


def test_price_overlap_free_and_unknown_values(client, event_factory):
    overlap = event_factory(price_min=500, price_max=1500)
    maximum_only = event_factory(price_min=None, price_max=1100)
    free = event_factory(price_min=None, price_max=None, is_free=True)
    event_factory(price_min=None, price_max=None, is_free=None, age_limit=None)
    assert set(
        ids(client.get("/events", params={"price_from": 1000, "price_to": 1200}))
    ) == {overlap, maximum_only}
    assert ids(client.get("/events", params={"price_to": 0})) == [free]
    assert ids(client.get("/events", params={"is_free": "true"})) == [free]
    assert len(ids(client.get("/events", params={"age_limit_max": 6}))) == 3


def test_radius_includes_nearby_and_excludes_unknown_coordinates(client, event_factory):
    nearby = event_factory()
    event_factory(latitude="53.6304", longitude="55.9273")
    event_factory(latitude=None, longitude=None)
    assert ids(
        client.get(
            "/events",
            params={"latitude": 54.7351, "longitude": 55.9587, "radius_km": 5},
        )
    ) == [nearby]


def test_free_event_with_unknown_prices_sorts_as_zero(client, event_factory):
    free = event_factory(price_min=None, price_max=None, is_free=True)
    paid = event_factory(price_min=100, price_max=200)
    unknown = event_factory(price_min=None, price_max=None, is_free=None)
    assert ids(client.get("/events", params={"sort": "price_asc"})) == [
        free,
        paid,
        unknown,
    ]
    assert ids(client.get("/events", params={"sort": "price_desc"})) == [
        paid,
        free,
        unknown,
    ]


@pytest.mark.parametrize(
    "sort, reverse",
    [
        ("date_asc", False),
        ("date_desc", True),
        ("price_asc", False),
        ("price_desc", True),
        ("updated_desc", True),
    ],
)
def test_sorts_put_null_values_last(client, event_factory, sort, reverse):
    earlier = event_factory(start=START, price_min=100, price_max=200, updated_at=START)
    later = event_factory(
        start=START + timedelta(days=1),
        end=START + timedelta(days=1, hours=2),
        price_min=300,
        price_max=400,
        updated_at=START + timedelta(days=1),
    )
    nulls = event_factory(
        start=None,
        end=None,
        status="Draft",
        price_min=None,
        price_max=None,
        updated_at=START - timedelta(days=1),
    )
    expected = [later, earlier] if reverse else [earlier, later]
    assert ids(
        client.get(
            "/events",
            params=[("sort", sort), ("statuses", "Published"), ("statuses", "Draft")],
        )
    ) == expected + [nulls]


def test_sql_and_like_characters_are_literal(client, event_factory, database):
    percent = event_factory(title="Скидка 100% на билеты")
    event_factory(title="Скидка 100 рублей")
    assert ids(client.get("/events", params={"title": "%"})) == [percent]
    assert ids(client.get("/events", params={"city": "Уфа' OR 1=1 --"})) == []
    assert (
        ids(client.get("/events", params={"q": "'; DROP TABLE parser_events; --"}))
        == []
    )
    with database[0].connect() as connection:
        assert connection.scalar(text("SELECT count(*) FROM parser_events")) == 2


def test_unknown_event_is_404(client):
    assert client.get(f"/events/{uuid4()}").status_code == 404
    assert client.get(f"/events/{uuid4()}/similar").status_code == 404


def test_schedule_half_open_interval_overlap_and_exact_venue(client, event_factory):
    overlap = event_factory(
        start=START - timedelta(hours=1), end=START + timedelta(minutes=30)
    )
    point = event_factory(start=START, end=None)
    inside = event_factory(
        start=START + timedelta(hours=1), end=START + timedelta(hours=2)
    )
    event_factory(
        start=START - timedelta(hours=2), end=START
    )  # End equals lower boundary.
    event_factory(
        start=START + timedelta(hours=3), end=None
    )  # Start equals upper boundary.
    event_factory(venue="Малый ГКЗ Башкортостан")
    event_factory(status="Cancelled")
    event_factory(status="Draft")
    event_factory(city="Другой город")
    response = client.get(
        "/venues/schedule",
        params=dict(
            venue="гкз БАШКОРТОСТАН",
            city="уфа",
            date_from=START.isoformat(),
            date_to=(START + timedelta(hours=3)).isoformat(),
        ),
    )
    assert ids(response) == [overlap, point, inside]
    assert response.json()["total"] == 3
    assert response.json()["date_from"].endswith("+05:00")
    assert response.json()["time_zone"] == "Asia/Yekaterinburg"


def test_schedule_date_only_multi_day_duration_and_pagination(client, event_factory):
    day = event_factory(end=None, date_start=None, start_time=None)
    exhibition = event_factory(
        end=None,
        date_start=None,
        start_time=None,
        start_date=START.date() - timedelta(days=2),
        end_date=START.date() + timedelta(days=1),
    )
    duration = event_factory(
        start=START - timedelta(hours=1), end=None, duration_min=180
    )
    event_factory(
        end=None,
        date_start=None,
        start_time=None,
        start_date=START.date() - timedelta(days=1),
    )
    params = dict(
        venue="ГКЗ Башкортостан",
        date_from=START.isoformat(),
        date_to=(START + timedelta(hours=2)).astimezone(UTC).isoformat(),
        page_size=2,
    )
    first = client.get("/venues/schedule", params=params)
    assert set(ids(first)) == {exhibition, duration}
    assert first.json()["has_next"] is True
    second = client.get("/venues/schedule", params={**params, "page": 2})
    assert ids(second) == [day]
    assert second.json()["items"][0]["date_start"] is None
    assert second.json()["items"][0]["start_time"] is None
    assert second.json()["total"] == 3
    assert ids(
        client.get(
            "/events",
            params={key: value for key, value in params.items() if key != "page_size"},
        )
    )


def test_empty_schedule(client):
    response = client.get(
        "/venues/schedule",
        params=dict(
            venue="Нет такой площадки",
            date_from=START.isoformat(),
            date_to=(START + timedelta(days=1)).isoformat(),
        ),
    )
    assert ids(response) == []
    assert response.json()["total"] == response.json()["pages"] == 0


def test_similar_ranks_features_excludes_reference_unrelated_past_and_unpublished(
    client, event_factory
):
    reference = event_factory()
    closest = event_factory()
    related = event_factory(
        title="Вечер саксофона", description="Саксофон и импровизация", tags=["джаз"]
    )
    event_factory(
        title="Детская экскурсия",
        category="Excursion",
        tags=["дети"],
        description="Поездка в зоопарк",
    )
    event_factory(status="Draft")
    event_factory(status="Cancelled")
    past = datetime.now(UTC) - timedelta(days=2)
    past_id = event_factory(start=past, end=past + timedelta(hours=2))
    response = client.get(f"/events/{reference}/similar")
    assert ids(response) == [closest, related]
    payload = response.json()
    assert payload["reference_event_id"] == reference
    assert payload["items"][0]["score"] == pytest.approx(1.0)
    assert payload["items"][0]["score"] > payload["items"][1]["score"] > 0
    assert payload["items"][0]["sources"]
    assert past_id in ids(
        client.get(f"/events/{reference}/similar", params={"include_past": "true"})
    )


def test_similar_supports_filters_threshold_and_pagination(client, event_factory):
    reference = event_factory()
    first, second = sorted([event_factory(), event_factory()])
    event_factory(city="Москва")
    path = f"/events/{reference}/similar"
    params = dict(city="Уфа", page_size=1, min_score="0.99")
    response = client.get(path, params=params)
    assert ids(response) == [first]
    assert response.json()["total"] == 2
    assert ids(client.get(path, params={**params, "page": 2})) == [second]
    assert client.get(path, params={"min_score": 2}).status_code == 422


def test_missing_similarity_features_do_not_match_on_empty_values(
    client, event_factory
):
    reference = event_factory(
        title="АБВ", category="Other", tags=[], description="", venue="Без площадки"
    )
    event_factory(
        title="XYZ", category="Other", tags=[], description="", venue="Без площадки"
    )
    assert (
        ids(client.get(f"/events/{reference}/similar", params={"min_score": 0})) == []
    )


def test_health_checks_database(client):
    assert client.get("/health").json() == {"status": "ok"}


def test_requests_use_read_only_repeatable_read_snapshot(client):
    @client.app.get("/_test_transaction")
    def transaction_flags(session: DatabaseSession):
        return {
            "isolation": session.scalar(text("SHOW transaction_isolation")),
            "read_only": session.scalar(text("SHOW transaction_read_only")),
        }

    assert client.get("/_test_transaction").json() == {
        "isolation": "repeatable read",
        "read_only": "on",
    }
