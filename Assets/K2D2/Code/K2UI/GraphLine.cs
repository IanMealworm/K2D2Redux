using Unity.Properties;
using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;

namespace K2UI.Graph
{
    // UXML only applies attributes present in the tag (see Group.cs), so the constructor sets every
    // attribute explicitly. MinY must stay below MaxY: equal values divide by zero in
    // value_to_pixel().
    [UxmlElement]
    public partial class GraphLine : VisualElement
    {
        // the orther is left, right, top, bottom
        public void setRanges(float min_x, float max_x, float min_y, float max_y)
        {
            MinX = min_x;
            MaxX = max_x;
            MinY = min_y;
            MaxY = max_y;
        }

        public float _min_x = 0;

        [CreateProperty]
        [UxmlAttribute("min-x")]
        public float MinX
        {
            get { return _min_x; }
            set { _min_x = value; MarkDirtyRepaint(); }
        }

        public float _max_x = 5;

        [CreateProperty]
        [UxmlAttribute("max-x")]
        public float MaxX
        {
            get { return _max_x; }
            set { _max_x = value; MarkDirtyRepaint(); }
        }

        public float _min_y = -1;

        [CreateProperty]
        [UxmlAttribute("min-y")]
        public float MinY
        {
            get { return _min_y; }
            set { _min_y = value; MarkDirtyRepaint(); }
        }

        public float _max_y = 1;

        [CreateProperty]
        [UxmlAttribute("max-y")]
        public float MaxY
        {
            get { return _max_y; }
            set { _max_y = value; MarkDirtyRepaint(); }
        }

        public Color _color = Color.white;

        [CreateProperty]
        [UxmlAttribute("line-color")]
        public Color LineColor  {
            get { return _color; }
            set { _color = value; MarkDirtyRepaint(); }
        }

        public float _line_width = 1;

        [CreateProperty]
        [UxmlAttribute("line-width")]
        public float LineWidth  {
            get { return _line_width; }
            set { _line_width = value; MarkDirtyRepaint(); }
        }

        public float _test_seed = -1;

        [CreateProperty]
        [UxmlAttribute("test-seed")]
        public float TestSeed  {
            get { return _test_seed; }
            set { _test_seed = value; 


            if (_test_seed >= 0)
            {
                rebuildtestLine();   
                MarkDirtyRepaint(); }
            }   
        }

        void rebuildtestLine()
        {
            int nb_point = 300;
            float max_x = 100;
            float period = 0.1f;

            points = new List<Vector2>(); 

            float x = 0; 
            float dx = max_x / nb_point;

            while (x <= max_x)
            {
                float y = Mathf.PerlinNoise(period*x, _test_seed);
                points.Add(new Vector2(x, y));
                x += dx;
            }

        }
        public GraphLine()
        {
           AddToClassList("graph-line");
           generateVisualContent += Draw;

           // Attribute defaults - see class comment.
           MinX = 0f;
           MaxX = 5f;
           MinY = -1f;
           MaxY = 1f;
           LineColor = Color.white;
           LineWidth = 1f;
           TestSeed = -1f;
        }

        List<Vector2> points = new();


        public void setSegment(Vector2 point_1, Vector2 point_2)
        {
            points.Clear();
            points.Add(point_1);
            points.Add(point_1);
            points.Add(point_2);
            MarkDirtyRepaint();
        }

        public void setPoints(List<Vector2> points)
        {
            this.points = points;
            MarkDirtyRepaint();
        }

        
        public float aspectRatio()
        {
            if (height_rect == 0) return 1;
            return width_rect / height_rect;
        }

        public float width_rect, height_rect;

        void compute_values()
        {
            Rect rect = contentRect;
            width_rect = rect.width;
            height_rect = rect.height;
        }

        Vector2 value_to_pixel(Vector2 value)
        {
            float ratio_x = (value.x - MinX)  /( MaxX - MinX );
            float ratio_y = (value.y - MinY)  /( MaxY - MinY );

            var res = new Vector2(
                width_rect * ratio_x, height_rect * ratio_y);
            return res;
        }

        void Draw(MeshGenerationContext ctx)
        {    
            compute_values();

            Painter2D painter = ctx.painter2D;

            if (points.Count < 2)
                return;

            painter.lineCap = LineCap.Round;

            painter.lineWidth = LineWidth;
            painter.strokeColor = LineColor;
            painter.BeginPath();

            painter.MoveTo(value_to_pixel(points[0]));
            for (int i = 1; i < points.Count; i++)
            {            
                painter.LineTo(value_to_pixel(points[i]));
            }

            painter.Stroke();
        }
    }
}