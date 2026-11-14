"""Unify independently merged search/parser and interaction histories.

No schema changes; both already published histories remain valid.
"""

revision = "0007_merge_service_heads"
down_revision = ("0006_merge_search_and_parser", "0006_interaction_service")
branch_labels = None
depends_on = None


def upgrade():
    pass


def downgrade():
    pass
