"""Shared setup for the upstream-SDK smoke suite.

The suite is configured only through the environment file that
eng/conformance/start-nachos-for-conformance.sh (Linux/macOS) or eng/conformance/Start-NachosForConformance.ps1
(Windows) writes; `--run` / `-Run` exports it. To run by hand in bash: `set -a; . <file>`.
"""

import os
import uuid
from dataclasses import dataclass

import pytest
from honcho import Honcho

# Honcho accepts a list call with no body; Nachos answers 422 json_invalid to the empty body that honcho-ai 2.5.1 sends
# when `filters` is None or empty (mismatch M1, see test_list_without_filters). Every other list call passes this
# filter, which matches every row, so the scenarios stay meaningful.
MATCH_ALL: dict[str, object] = {"metadata": {}}


@dataclass(frozen=True)
class Nachos:
    base_url: str
    admin_key: str
    auth_mode: str
    peer_key: str
    auth_workspace: str
    auth_peer: str

    def client(self, workspace_id: str, api_key: str | None = None, **options) -> Honcho:
        return Honcho(
            base_url=self.base_url,
            workspace_id=workspace_id,
            api_key=self.admin_key if api_key is None else api_key,
            **options,
        )


def pytest_configure(config):
    missing = [name for name in ("NACHOS_BASE_URL", "NACHOS_ADMIN_KEY") if not os.environ.get(name)]
    if missing:
        pytest.exit(
            f"{', '.join(missing)} not set: run eng/conformance/start-nachos-for-conformance.sh --run",
            returncode=2,
        )


@pytest.fixture(scope="session")
def nachos() -> Nachos:
    env = os.environ
    return Nachos(
        base_url=env["NACHOS_BASE_URL"],
        admin_key=env["NACHOS_ADMIN_KEY"],
        auth_mode=env.get("NACHOS_AUTH_MODE", "disabled"),
        peer_key=env.get("NACHOS_PEER_KEY", ""),
        auth_workspace=env.get("NACHOS_AUTH_WORKSPACE", "conformance-auth"),
        auth_peer=env.get("NACHOS_AUTH_PEER", "member"),
    )


@pytest.fixture
def workspace_id() -> str:
    # A fresh workspace per test: scenarios never see each other's rows.
    return f"py-{uuid.uuid4().hex[:12]}"


@pytest.fixture
def honcho(nachos: Nachos, workspace_id: str) -> Honcho:
    return nachos.client(workspace_id, timeout=10)
