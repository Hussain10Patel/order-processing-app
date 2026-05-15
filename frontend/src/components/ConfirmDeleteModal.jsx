function ConfirmDeleteModal({
  open,
  title = "Confirm Delete",
  message = "Are you sure you want to delete this?",
  confirmText = "Delete",
  cancelText = "Cancel",
  confirming = false,
  onConfirm,
  onCancel,
}) {
  if (!open) {
    return null;
  }

  return (
    <div className="modal-overlay" role="presentation">
      <div className="modal-card" role="dialog" aria-modal="true" aria-labelledby="confirm-delete-title">
        <h3 id="confirm-delete-title">{title}</h3>
        <p className="status-text" style={{ marginTop: 8 }}>{message}</p>

        <div className="modal-actions">
          <button type="button" className="secondary" onClick={onCancel} disabled={confirming}>
            {cancelText}
          </button>
          <button type="button" className="danger" onClick={onConfirm} disabled={confirming}>
            {confirming ? "Deleting..." : confirmText}
          </button>
        </div>
      </div>
    </div>
  );
}

export default ConfirmDeleteModal;
