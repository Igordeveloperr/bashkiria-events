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
