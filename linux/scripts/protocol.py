"""Public envelope validation; synthetic examples never imply game execution."""
SCHEMA = 'sts2-action-log-v1'
KINDS = {'initiated', 'accepted', 'delivered', 'completed', 'rejected', 'failed', 'interrupted'}

def validate(row):
    required = {'schema', 'rootRunId', 'processRunId', 'slAttempt', 'recovery', 'sequence', 'status', 'action', 'observation', 'error'}
    if set(row) != required or row['schema'] != SCHEMA or row['status'] not in KINDS:
        raise ValueError('Unknown or incomplete action-log envelope')
    if not all(isinstance(row[k], str) and row[k] for k in ('rootRunId', 'processRunId')):
        raise ValueError('Run/process identity required')
    if type(row['sequence']) is not int or row['sequence'] < 0 or type(row['slAttempt']) is not int or row['slAttempt'] != 0 or row['recovery'] is not None:
        raise ValueError('Unsupported sequence/SL/recovery')
    if any(row[k] is not None and not isinstance(row[k], dict) for k in ('action', 'observation')) or (row['error'] is not None and not isinstance(row['error'], str)):
        raise ValueError('Invalid action/observation/error type')
    if row['status'] in {'initiated', 'accepted', 'delivered', 'completed'} and not isinstance(row['action'], dict):
        raise ValueError('Action identity required')
    if row['status'] == 'completed' and not isinstance(row['observation'], dict):
        raise ValueError('Successor observation required')
    if row['status'] in {'rejected', 'failed', 'interrupted'} and not row['error']:
        raise ValueError('Explicit failure/interruption reason required')
    return row
