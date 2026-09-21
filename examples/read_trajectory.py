#!/usr/bin/env python3
"""Print accepted decisions and their observed successors from an action log."""
import argparse
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "linux/scripts"))
from protocol import validate


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path)
    args = parser.parse_args()
    pending = {}
    outcomes = []
    for line in args.log.read_text().splitlines():
        row = validate(json.loads(line))
        key = (row["processRunId"], row["sequence"])
        if row["status"] == "accepted":
            if key in pending:
                raise ValueError("Duplicate accepted decision: " + str(key))
            pending[key] = row
        elif row["status"] == "completed":
            before = pending.pop(key)
            if before["action"] != row["action"]:
                raise ValueError("Successor action differs: " + str(key))
            print(json.dumps({
                "processRunId": key[0], "sequence": key[1],
                "observation": before["observation"],
                "action": before["action"],
                "nextObservation": row["observation"],
            }, ensure_ascii=False))
        elif row["status"] in {"rejected", "failed", "interrupted"}:
            outcomes.append({"processRunId": key[0], "sequence": key[1],
                             "status": row["status"], "error": row["error"]})
    print(json.dumps({"inputsWithoutSuccessor": [list(key) for key in pending],
                      "outcomes": outcomes}), file=sys.stderr)


if __name__ == "__main__":
    main()
