"""Join the existing parser/search merge and interaction migration heads.

Revision ID: 0007_merge_service_heads
Revises: 0006_merge_search_and_parser, 0006_interaction_service
"""

revision = "0007_merge_service_heads"
down_revision = ("0006_merge_search_and_parser", "0006_interaction_service")
branch_labels = None
depends_on = None


def upgrade():
    pass


def downgrade():
    pass
