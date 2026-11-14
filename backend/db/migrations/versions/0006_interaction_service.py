"""Merge parser/search heads and add subscriptions, notification outbox and feedback.

Revision ID: 0006_interaction_service
Revises: 0005_parser_references, 0004_event_search
"""
from alembic import op
import sqlalchemy as sa

revision = "0006_interaction_service"
down_revision = ("0005_parser_references", "0004_event_search")
branch_labels = None
depends_on = None


def uid(name="id", nullable=False, parent=None, ondelete="CASCADE"):
    args = [sa.ForeignKey(parent, ondelete=ondelete)] if parent else []
    return sa.Column(name, sa.Uuid(), *args, nullable=nullable, primary_key=name == "id")


def text(name, nullable=False):
    return sa.Column(name, sa.Text(), nullable=nullable)


def timestamp(name, nullable=False):
    return sa.Column(name, sa.DateTime(timezone=True), nullable=nullable)


def version():
    return sa.Column("version", sa.BigInteger(), nullable=False)


def upgrade():
    op.create_table("interaction_recipients", uid(), text("user_id"), text("channel"), timestamp("next_digest_at"), version(),
        sa.CheckConstraint("channel IN ('Web','Telegram')", name="interaction_recipient_channel"),
        sa.CheckConstraint("length(user_id) BETWEEN 1 AND 128", name="interaction_recipient_user"))
    op.create_index("interaction_recipients_identity", "interaction_recipients", ["user_id", "channel"], unique=True)
    op.create_table("interaction_subscriptions", uid(), uid("recipient_id", parent="interaction_recipients.id"),
        uid("city_id", parent="parser_cities.id", ondelete="RESTRICT"), text("category", nullable=True),
        sa.Column("date_from", sa.Date()), sa.Column("date_to", sa.Date()), text("filter_hash"),
        timestamp("created_at"), timestamp("updated_at"), version(),
        sa.CheckConstraint("date_to IS NULL OR date_from IS NULL OR date_to >= date_from", name="interaction_subscription_dates"),
        sa.CheckConstraint("category IS NULL OR category IN ('Other','Concert','Theatre','Exhibition','Festival','Cinema','Excursion','Workshop','Lecture')", name="interaction_subscription_category"))
    op.create_index("interaction_subscriptions_filter", "interaction_subscriptions", ["recipient_id", "filter_hash"], unique=True)
    op.create_table("interaction_notifications", uid(), uid("recipient_id", parent="interaction_recipients.id"), text("status"),
        timestamp("created_at"), timestamp("available_at"), timestamp("delivered_at", True), timestamp("read_at", True),
        uid("lease_token", True), timestamp("lease_expires_at", True), sa.Column("attempts", sa.Integer(), nullable=False),
        text("last_error_code", True), version(),
        sa.CheckConstraint("status IN ('Pending','Leased','Delivered','Suppressed','Failed')", name="interaction_notification_status"),
        sa.CheckConstraint("attempts >= 0", name="interaction_notification_attempts"),
        sa.CheckConstraint("status <> 'Leased' OR (lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)", name="interaction_notification_lease"))
    op.create_index("interaction_notifications_queue", "interaction_notifications", ["status", "available_at"])
    op.create_index("interaction_notifications_inbox", "interaction_notifications", ["recipient_id", "created_at"])
    op.create_table("interaction_notification_events", uid(),
        uid("notification_id", True, parent="interaction_notifications.id", ondelete="SET NULL"),
        uid("subscription_id", parent="interaction_subscriptions.id"), uid("event_id"))
    op.create_index("interaction_notification_events_once", "interaction_notification_events", ["subscription_id", "event_id"], unique=True)
    op.create_table("interaction_feedback", uid(), text("user_id"), uid("request_key"), text("content_hash"), text("kind"),
        uid("event_id", True), text("subject"), text("message"), text("status"), timestamp("created_at"), timestamp("updated_at"), version(),
        sa.CheckConstraint("kind IN ('Question','Correction','OrganizerApplication')", name="interaction_feedback_kind"),
        sa.CheckConstraint("status IN ('New','InProgress','Resolved','Rejected')", name="interaction_feedback_status"),
        sa.CheckConstraint("length(subject) BETWEEN 1 AND 200 AND length(message) BETWEEN 1 AND 5000", name="interaction_feedback_text"))
    op.create_index("interaction_feedback_request", "interaction_feedback", ["user_id", "request_key"], unique=True)
    op.create_index("interaction_feedback_user_date", "interaction_feedback", ["user_id", "created_at"])
    op.create_index("interaction_feedback_queue", "interaction_feedback", ["status", "created_at"])
    op.create_table("interaction_feedback_reviews", uid(), uid("feedback_id", parent="interaction_feedback.id"),
        text("previous_status"), text("status"), text("note"), text("actor"), timestamp("created_at"),
        sa.CheckConstraint("length(note) BETWEEN 1 AND 2000", name="interaction_feedback_review_note"))


def downgrade():
    for table in ("interaction_feedback_reviews", "interaction_feedback", "interaction_notification_events",
                  "interaction_notifications", "interaction_subscriptions", "interaction_recipients"):
        op.drop_table(table)
