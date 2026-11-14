import pytest
from fastapi.testclient import TestClient
from sqlalchemy.exc import OperationalError

from searcher.config import Settings
from searcher.database import get_session
from searcher.main import create_app


@pytest.fixture
def validation_client():
    app = create_app(Settings(_env_file=None))
    app.dependency_overrides[get_session] = lambda: None
    with TestClient(app) as client:
        yield client


@pytest.mark.parametrize(
    "params",
    [
        {"page": 0},
        {"offset": -1},
        {"page_size": 501},
        {"page_size": 0},
        {"unexpected": "x"},
        {"categories": "Unknown"},
        {"statuses": "Wrong"},
        {"source_statuses": "Wrong"},
        {"date_from": "2035-01-10T18:00:00"},
        {"date_from": "2035-01-11T00:00:00Z", "date_to": "2035-01-10T00:00:00Z"},
        {"start_date_from": "2035-01-11", "start_date_to": "2035-01-10"},
        {"price_from": "200", "price_to": "100"},
        {"price_to": "NaN"},
        {"price_to": "-1"},
        {"duration_from": 90, "duration_to": 60},
        {"age_limit_max": 19},
        {"latitude": 54.7},
        {"latitude": 100, "longitude": 55, "radius_km": 10},
        {"confidence_min": "Infinity"},
        {"sort": "relevance"},
        {"q": "   "},
        {"short": "invalid"},
    ],
)
def test_invalid_search_returns_422(validation_client, params):
    response = validation_client.get("/events", params=params)
    assert response.status_code == 422, response.text


@pytest.mark.parametrize(
    "params",
    [
        {},
        {"venue": "ГКЗ"},
        {
            "venue": "ГКЗ",
            "date_from": "2035-01-10T18:00:00",
            "date_to": "2035-01-11T18:00:00Z",
        },
        {
            "venue": "ГКЗ",
            "date_from": "2035-01-10T18:00:00Z",
            "date_to": "2035-01-10T18:00:00Z",
        },
    ],
)
def test_invalid_schedule_returns_422(validation_client, params):
    assert validation_client.get("/venues/schedule", params=params).status_code == 422


def test_openapi_has_named_get_operations_and_query_arrays(validation_client):
    document = validation_client.get("/openapi.json").json()
    fields = {
        "id",
        "title",
        "category",
        "start_date",
        "start_time",
        "venue",
        "age_limit",
    }
    schemas = document["components"]["schemas"]
    assert set(schemas["ShortEventResponse"]["properties"]) == fields
    assert set(schemas["ShortSimilarEventResponse"]["properties"]) == fields | {"score"}
    assert fields < set(schemas["EventResponse"]["properties"])
    operations = {
        entry["get"]["operationId"]: entry["get"]
        for entry in document["paths"].values()
    }
    assert {
        "search_events",
        "get_venue_schedule",
        "find_similar",
        "get_event",
    } <= operations.keys()
    for operation in operations.values():
        assert "requestBody" not in operation
    params = {item["name"]: item for item in operations["search_events"]["parameters"]}
    assert params["tags"]["in"] == "query"
    assert params["tags"]["schema"]["type"] == "array"
    assert params["short"]["schema"]["type"] == "boolean"
    assert params["short"]["schema"]["default"] is False
    response_schema = operations["search_events"]["responses"]["200"]["content"][
        "application/json"
    ]["schema"]
    assert {item["$ref"] for item in response_schema["anyOf"]} == {
        "#/components/schemas/EventPage",
        "#/components/schemas/ShortEventPage",
    }
    similar_params = {
        item["name"]: item for item in operations["find_similar"]["parameters"]
    }
    assert set(similar_params) == {
        "event_id",
        "page",
        "page_size",
        "offset",
        "min_score",
        "city",
        "date_from",
        "date_to",
        "public_only",
        "short",
    }
    assert similar_params["min_score"]["schema"]["default"] == 0.15
    assert similar_params["short"]["schema"]["default"] is False
    similar_response = operations["find_similar"]["responses"]["200"]["content"][
        "application/json"
    ]["schema"]
    assert {item["$ref"] for item in similar_response["anyOf"]} == {
        "#/components/schemas/SimilarPage",
        "#/components/schemas/ShortSimilarPage",
    }


@pytest.mark.parametrize(
    "params",
    [
        {"q": "сказка"},
        {"statuses": "Published"},
        {"include_past": "true"},
        {"short": "invalid"},
    ],
)
def test_similar_does_not_accept_search_filters(validation_client, params):
    response = validation_client.get(
        "/events/ec0e76ba-9244-46ae-ab4b-fdbb8b3e1020/similar", params=params
    )
    assert response.status_code == 422


@pytest.mark.parametrize("min_score", [-0.01, 1.01, "NaN", "Infinity", "-Infinity"])
def test_similar_rejects_invalid_min_score(validation_client, min_score):
    response = validation_client.get(
        "/events/ec0e76ba-9244-46ae-ab4b-fdbb8b3e1020/similar",
        params={"min_score": min_score},
    )
    assert response.status_code == 422


def test_database_failure_is_503_without_connection_details(validation_client):
    def unavailable():
        raise OperationalError(
            "secret SQL",
            {"password": "secret-token"},
            RuntimeError("connection failed"),
        )

    validation_client.app.dependency_overrides[get_session] = unavailable
    response = validation_client.get("/events")
    assert response.status_code == 503
    assert "secret" not in response.text


def test_invalid_event_uuid_returns_422(validation_client):
    assert validation_client.get("/events/not-a-uuid").status_code == 422


def test_gateway_prefix_is_used_by_swagger_and_openapi():
    app = create_app(Settings(_env_file=None, root_path="/search"))
    with TestClient(app) as client:
        response = client.get("/docs")
        assert response.status_code == 200
        assert "/search/openapi.json" in response.text
        schema = client.get("/openapi.json").json()
        assert {"url": "/search"} in schema["servers"]
        assert "/events" in schema["paths"]
