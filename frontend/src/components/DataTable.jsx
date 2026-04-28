import { memo } from "react";

function DataTable({
  columns,
  data,
  rowKey,
  onRowClick,
  rowClassName,
  sortKey,
  sortDirection,
  onSort,
}) {
  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            {columns.map((column) => {
              const isActiveSort = sortKey === column.key;
              const direction = isActiveSort ? sortDirection : "";

              return (
                <th key={column.key}>
                  {column.sortable ? (
                    <button type="button" onClick={() => onSort(column.key)}>
                      {column.header} {direction ? (direction === "asc" ? "^" : "v") : ""}
                    </button>
                  ) : (
                    column.header
                  )}
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {data.map((row, rowIndex) => (
            <tr
              key={row[rowKey]}
              className={`${onRowClick ? "clickable" : ""} ${rowClassName ? rowClassName(row) : ""}`.trim()}
              tabIndex={onRowClick ? 0 : undefined}
              onClick={onRowClick ? () => onRowClick(row) : undefined}
              onKeyDown={
                onRowClick
                  ? (event) => {
                      if (event.key === "Enter" || event.key === " ") {
                        event.preventDefault();
                        onRowClick(row);
                      }
                    }
                  : undefined
              }
            >
              {columns.map((column) => (
                <td key={`${row[rowKey]}-${column.key}`}>
                  {column.render ? column.render(row, rowIndex) : row[column.key]}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export default memo(DataTable);
