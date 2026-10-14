import Foundation

protocol Shape {
    func area() -> Double
    func describe(prefix: String) -> String
}

struct Circle: Shape {
    let radius: Double

    func area() -> Double {
        return Double.pi * radius * radius
    }

    func describe(prefix: String) -> String {
        return "\(prefix) circle"
    }
}

final class ShapeStore<Element: Shape> {
    private var shapes: [Element] = []

    func add(_ shape: Element) {
        shapes.append(shape)
    }

    func add(contentsOf more: [Element]) {
        shapes.append(contentsOf: more)
    }

    enum SortOrder {
        case ascending
        case descending

        func reversed() -> SortOrder {
            return self == .ascending ? .descending : .ascending
        }
    }
}

enum Unit: String {
    case metric
    case imperial
}

actor Counter {
    var value = 0

    func increment() {
        value += 1
    }
}

extension Circle {
    func diameter() -> Double {
        return radius * 2
    }
}

func totalArea(of shapes: [Shape]) -> Double {
    return shapes.reduce(0) { $0 + $1.area() }
}
