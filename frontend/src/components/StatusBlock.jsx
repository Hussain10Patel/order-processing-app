function StatusBlock({ loading, error, empty, loadingText, emptyText, spinner }) {
  if (loading) {
    return (
      <p className="status-text">
        {spinner && <span className="spinner" aria-hidden="true" />}
        {loadingText ?? "Loading..."}
      </p>
    );
  }

  if (error) {
    return <p className="status-text error">{error}</p>;
  }

  if (empty) {
    return <p className="status-text">{emptyText ?? "No data found"}</p>;
  }

  return null;
}

export default StatusBlock;
