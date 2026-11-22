// Query Gate - Interactive Entity-Relationship (ER) Diagram Engine
(function () {
    window.dbamEr = {
        instances: {},

        render: function (containerId, data, dotnetHelper) {
            var container = document.getElementById(containerId);
            if (!container) return;

            this.destroy(containerId);

            var state = {
                container: container,
                data: data,
                dotnetHelper: dotnetHelper,
                zoom: 1.0,
                panX: 40,
                panY: 40,
                isPanning: false,
                panStartX: 0,
                panStartY: 0,
                draggingNode: null,
                dragStartX: 0,
                dragStartY: 0,
                nodePositions: {},
                selectedTable: null
            };

            this.instances[containerId] = state;
            this.layoutNodes(state);
            this.draw(state);
            this.bindEvents(state);
        },

        layoutNodes: function (state) {
            var nodes = state.data.nodes || [];
            var cols = Math.max(1, Math.ceil(Math.sqrt(nodes.length * 1.5)));
            var colWidth = 320;
            var rowHeight = 280;

            for (var i = 0; i < nodes.length; i++) {
                var node = nodes[i];
                var c = i % cols;
                var r = Math.floor(i / cols);
                state.nodePositions[node.fullName] = {
                    x: 60 + c * colWidth,
                    y: 60 + r * rowHeight,
                    width: 250,
                    height: 50 + Math.min(node.columns.length, 12) * 22
                };
            }
        },

        draw: function (state) {
            var container = state.container;
            container.innerHTML = "";
            container.style.position = "relative";
            container.style.overflow = "hidden";
            container.style.userSelect = "none";
            container.style.cursor = state.isPanning ? "grabbing" : "grab";

            var world = document.createElement("div");
            world.className = "er-world";
            world.style.position = "absolute";
            world.style.transformOrigin = "0 0";
            world.style.transform = "translate(" + state.panX + "px, " + state.panY + "px) scale(" + state.zoom + ")";
            world.style.width = "4000px";
            world.style.height = "4000px";
            state.world = world;

            // SVG layer for edges
            var svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
            svg.setAttribute("width", "4000");
            svg.setAttribute("height", "4000");
            svg.style.position = "absolute";
            svg.style.top = "0";
            svg.style.left = "0";
            svg.style.pointerEvents = "none";

            var defs = document.createElementNS("http://www.w3.org/2000/svg", "defs");
            defs.innerHTML = `
                <marker id="er-arrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
                    <path d="M 0 1 L 9 5 L 0 9 z" fill="#5b5bf0" />
                </marker>
                <marker id="er-arrow-active" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
                    <path d="M 0 1 L 9 5 L 0 9 z" fill="#ef4444" />
                </marker>
            `;
            svg.appendChild(defs);

            var edgeGroup = document.createElementNS("http://www.w3.org/2000/svg", "g");
            edgeGroup.className = "er-edges";
            svg.appendChild(edgeGroup);
            state.edgeGroup = edgeGroup;
            world.appendChild(svg);

            // Table cards
            var nodes = state.data.nodes || [];
            for (var i = 0; i < nodes.length; i++) {
                var node = nodes[i];
                var card = this.createTableCard(state, node);
                world.appendChild(card);
            }

            container.appendChild(world);
            this.updateEdges(state);
        },

        createTableCard: function (state, node) {
            var pos = state.nodePositions[node.fullName] || { x: 50, y: 50, width: 250, height: 200 };
            var isFocus = state.data.focusTable && state.data.focusTable.toLowerCase() === node.fullName.toLowerCase();

            var card = document.createElement("div");
            card.className = "er-table-card" + (isFocus ? " focus-table" : "");
            card.id = "er-node-" + encodeURIComponent(node.fullName);
            card.style.position = "absolute";
            card.style.left = pos.x + "px";
            card.style.top = pos.y + "px";
            card.style.width = pos.width + "px";
            card.style.background = "var(--bg-elev, #ffffff)";
            card.style.border = isFocus ? "2px solid var(--brand, #5b5bf0)" : "1px solid var(--line, #e3e6f1)";
            card.style.borderRadius = "8px";
            card.style.boxShadow = isFocus ? "0 4px 16px rgba(91,91,240,0.2)" : "0 2px 8px rgba(0,0,0,0.06)";
            card.style.cursor = "move";
            card.style.zIndex = "10";
            card.style.fontSize = "12px";

            var header = document.createElement("div");
            header.style.padding = "8px 10px";
            header.style.background = isFocus ? "var(--brand-soft, #ececff)" : "var(--bg-sunken, #f5f6fb)";
            header.style.borderTopLeftRadius = "7px";
            header.style.borderTopRightRadius = "7px";
            header.style.borderBottom = "1px solid var(--line, #e3e6f1)";
            header.style.display = "flex";
            header.style.justifyContent = "space-between";
            header.style.alignItems = "center";

            var titleBox = document.createElement("div");
            titleBox.style.overflow = "hidden";
            titleBox.style.textOverflow = "ellipsis";
            titleBox.style.whiteSpace = "nowrap";

            if (node.schema) {
                var schemaSpan = document.createElement("span");
                schemaSpan.style.color = "var(--text-faint, #8a91b0)";
                schemaSpan.style.fontSize = "10px";
                schemaSpan.textContent = node.schema + ".";
                titleBox.appendChild(schemaSpan);
            }

            var nameSpan = document.createElement("b");
            nameSpan.style.color = "var(--text, #151a33)";
            nameSpan.textContent = node.name;
            titleBox.appendChild(nameSpan);
            header.appendChild(titleBox);

            var qBtn = document.createElement("button");
            qBtn.type = "button";
            qBtn.title = "Open query tab for this table";
            qBtn.style.padding = "2px 6px";
            qBtn.style.fontSize = "10px";
            qBtn.style.border = "1px solid var(--line, #e3e6f1)";
            qBtn.style.borderRadius = "4px";
            qBtn.style.background = "var(--bg-elev, #ffffff)";
            qBtn.style.cursor = "pointer";
            qBtn.textContent = "Query";
            qBtn.onmousedown = function (e) { e.stopPropagation(); };
            qBtn.onclick = function (e) {
                e.stopPropagation();
                if (state.dotnetHelper) {
                    state.dotnetHelper.invokeMethodAsync("OnSelectTableQuery", node.fullName);
                }
            };
            header.appendChild(qBtn);
            card.appendChild(header);

            // Columns
            var colList = document.createElement("div");
            colList.style.maxHeight = "220px";
            colList.style.overflowY = "auto";
            colList.style.padding = "4px 0";

            for (var c = 0; c < node.columns.length; c++) {
                var col = node.columns[c];
                var row = document.createElement("div");
                row.id = "col-" + encodeURIComponent(node.fullName + "." + col.name);
                row.style.display = "flex";
                row.style.justifyContent = "space-between";
                row.style.alignItems = "center";
                row.style.padding = "3px 10px";
                row.style.fontSize = "11px";
                row.style.borderBottom = "1px dashed var(--line, #f0f2f8)";

                var left = document.createElement("div");
                left.style.display = "inline-flex";
                left.style.alignItems = "center";
                left.style.gap = "4px";

                if (col.isPrimaryKey) {
                    var pkBadge = document.createElement("span");
                    pkBadge.style.color = "#eab308";
                    pkBadge.style.fontWeight = "bold";
                    pkBadge.title = "Primary Key";
                    pkBadge.textContent = "[PK]";
                    left.appendChild(pkBadge);
                }
                if (col.isForeignKey) {
                    var fkBadge = document.createElement("span");
                    fkBadge.style.color = "var(--brand, #5b5bf0)";
                    fkBadge.style.fontWeight = "bold";
                    fkBadge.title = "Foreign Key";
                    fkBadge.textContent = "[FK]";
                    left.appendChild(fkBadge);
                }

                var colName = document.createElement("span");
                colName.style.color = "var(--text, #151a33)";
                colName.textContent = col.name;
                left.appendChild(colName);
                row.appendChild(left);

                var colType = document.createElement("span");
                colType.style.color = "var(--text-faint, #8a91b0)";
                colType.style.fontFamily = "monospace";
                colType.style.fontSize = "10px";
                colType.textContent = col.dataType + (col.nullable ? "?" : "");
                row.appendChild(colType);

                colList.appendChild(row);
            }
            card.appendChild(colList);

            // Drag binding
            var self = this;
            header.onmousedown = function (e) {
                if (e.button !== 0) return;
                state.draggingNode = node.fullName;
                state.dragStartX = e.clientX - pos.x;
                state.dragStartY = e.clientY - pos.y;
                card.style.zIndex = "100";
                e.stopPropagation();
            };

            card.onclick = function (e) {
                state.selectedTable = node.fullName;
                self.highlightTable(state, node.fullName);
            };

            return card;
        },

        updateEdges: function (state) {
            var group = state.edgeGroup;
            if (!group) return;
            group.innerHTML = "";

            var edges = state.data.edges || [];
            for (var i = 0; i < edges.length; i++) {
                var edge = edges[i];
                var fromPos = state.nodePositions[edge.fromTable];
                var toPos = state.nodePositions[edge.toTable];
                if (!fromPos || !toPos) continue;

                var isSelected = state.selectedTable &&
                    (state.selectedTable.toLowerCase() === edge.fromTable.toLowerCase() ||
                     state.selectedTable.toLowerCase() === edge.toTable.toLowerCase());

                var x1 = fromPos.x + fromPos.width;
                var y1 = fromPos.y + 40;
                var x2 = toPos.x;
                var y2 = toPos.y + 40;

                if (fromPos.x > toPos.x + toPos.width) {
                    x1 = fromPos.x;
                    x2 = toPos.x + toPos.width;
                }

                var dx = Math.abs(x2 - x1) * 0.5;
                var cx1 = x1 + (x2 > x1 ? dx : -dx);
                var cy1 = y1;
                var cx2 = x2 - (x2 > x1 ? dx : -dx);
                var cy2 = y2;

                var path = document.createElementNS("http://www.w3.org/2000/svg", "path");
                var d = "M " + x1 + " " + y1 + " C " + cx1 + " " + cy1 + ", " + cx2 + " " + cy2 + ", " + x2 + " " + y2;
                path.setAttribute("d", d);
                path.setAttribute("fill", "none");
                path.setAttribute("stroke", isSelected ? "#ef4444" : "#5b5bf0");
                path.setAttribute("stroke-width", isSelected ? "2.5" : "1.5");
                path.setAttribute("stroke-dasharray", isSelected ? "none" : "4,2");
                path.setAttribute("marker-end", isSelected ? "url(#er-arrow-active)" : "url(#er-arrow)");
                path.setAttribute("opacity", isSelected ? "1.0" : "0.7");

                group.appendChild(path);
            }
        },

        highlightTable: function (state, tableName) {
            this.updateEdges(state);
        },

        bindEvents: function (state) {
            var container = state.container;
            var self = this;

            container.onmousedown = function (e) {
                if (e.target !== container && e.target.tagName !== "svg") return;
                state.isPanning = true;
                state.panStartX = e.clientX - state.panX;
                state.panStartY = e.clientY - state.panY;
                container.style.cursor = "grabbing";
            };

            window.addEventListener("mousemove", function (e) {
                if (state.isPanning) {
                    state.panX = e.clientX - state.panStartX;
                    state.panY = e.clientY - state.panStartY;
                    state.world.style.transform = "translate(" + state.panX + "px, " + state.panY + "px) scale(" + state.zoom + ")";
                } else if (state.draggingNode) {
                    var pos = state.nodePositions[state.draggingNode];
                    if (pos) {
                        pos.x = (e.clientX - state.dragStartX);
                        pos.y = (e.clientY - state.dragStartY);
                        var card = document.getElementById("er-node-" + encodeURIComponent(state.draggingNode));
                        if (card) {
                            card.style.left = pos.x + "px";
                            card.style.top = pos.y + "px";
                        }
                        self.updateEdges(state);
                    }
                }
            });

            window.addEventListener("mouseup", function () {
                if (state.isPanning) {
                    state.isPanning = false;
                    container.style.cursor = "grab";
                }
                if (state.draggingNode) {
                    var card = document.getElementById("er-node-" + encodeURIComponent(state.draggingNode));
                    if (card) card.style.zIndex = "10";
                    state.draggingNode = null;
                }
            });

            container.onwheel = function (e) {
                e.preventDefault();
                var delta = e.deltaY > 0 ? 0.9 : 1.1;
                self.setZoom(state.container.id, state.zoom * delta);
            };
        },

        setZoom: function (containerId, zoomLevel) {
            var state = this.instances[containerId];
            if (!state) return;
            state.zoom = Math.max(0.2, Math.min(2.5, zoomLevel));
            state.world.style.transform = "translate(" + state.panX + "px, " + state.panY + "px) scale(" + state.zoom + ")";
        },

        zoomIn: function (containerId) {
            var state = this.instances[containerId];
            if (state) this.setZoom(containerId, state.zoom * 1.2);
        },

        zoomOut: function (containerId) {
            var state = this.instances[containerId];
            if (state) this.setZoom(containerId, state.zoom * 0.8);
        },

        zoomFit: function (containerId) {
            var state = this.instances[containerId];
            if (!state) return;
            state.panX = 40;
            state.panY = 40;
            state.zoom = 1.0;
            state.world.style.transform = "translate(" + state.panX + "px, " + state.panY + "px) scale(1.0)";
        },

        destroy: function (containerId) {
            if (this.instances[containerId]) {
                delete this.instances[containerId];
            }
        }
    };
})();
