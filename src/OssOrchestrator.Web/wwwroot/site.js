// Labels each table cell with its column header, so the stacked card layout on a phone can show it.
document.querySelectorAll("table").forEach(function (table) {
    var heads = Array.prototype.map.call(table.querySelectorAll("thead th"), function (th) { return th.textContent.trim(); });
    table.querySelectorAll("tbody tr").forEach(function (row) {
        Array.prototype.forEach.call(row.children, function (cell, i) { cell.setAttribute("data-label", heads[i] || ""); });
    });
});
